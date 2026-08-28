using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Genel Bakış -- KULLANICI ISTEGI (2026-08-28): "gönderim durumları, başarı oranı, bugün kaç
// protokol var kaçı gönderildi... derin ve göz boyayan bir dashboard". Onceki artifact mockup'i
// (bkz. konusma) buranin gorsel/yapisal temelidir -- ayni CSS token'lari (site.css) ve ayni
// bilgi mimarisi burada GERCEK veriyle dolduruluyor. Renklendirme (basarili/uyari/kritik)
// KULLANICI ISTEGI'ne (2026-08-28) gore kutularin bir kenarina da tasindi (accent-* siniflari,
// bkz. site.css), sadece ikon tonlamasiyla yetinilmedi.
public class GenelBakisModel(PusulaRepository pusulaRepository, SyncLogStore syncLog) : PageModel
{
    // Aktivite/Doktorlar'daki ResourceType siralamasiyla AYNI -- kullanicinin diger
    // ekranlarda gordugu sirayla eslessin diye (Hasta -> Muayine -> Tani -> Islem -> Epikriz).
    private static readonly string[] TrackedResourceTypes =
        ["Patient", "Practitioner", "Encounter", "Condition", "Procedure", "Composition"];

    [BindProperty(SupportsGet = true)]
    public string Donem { get; set; } = "7"; // "0" Bugün, "7" Son 7 Gün, "30" Son 30 Gün

    public DateTime PeriodFromUtc { get; private set; }
    public DateTime PeriodToUtcExclusive { get; private set; }
    public DateOnly PeriodFromDate { get; private set; }
    public DateOnly PeriodToDate { get; private set; }

    public int TodayProtocolCount { get; set; }
    public double TodayProtocolChangePct { get; set; }
    public bool TodayProtocolHasBaseline { get; set; }
    public int TodaySentCount { get; set; }
    public double OverallSuccessRatePct { get; set; }
    public double SuccessRateDeltaPct { get; set; }
    public int FailedCount { get; set; }
    public int PendingCount { get; set; }

    public double TodaySentPct => TodayProtocolCount == 0 ? 0 : Math.Round(TodaySentCount * 100.0 / TodayProtocolCount, 0);
    public double SuccessRateRingDashOffset => 119.4 * (1 - OverallSuccessRatePct / 100.0);
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public List<DailyTrendPoint> Trend { get; set; } = [];
    public List<ResourceBreakdownRow> Breakdown { get; set; } = [];
    public List<SyncLogEntry> RecentErrors { get; set; } = [];
    public List<DeptVolumeRow> DeptVolume { get; set; } = [];
    public List<NotReadyGroup> NotReadyGroups { get; set; } = [];
    public List<ErrorCategoryGroup> ErrorCategories { get; set; } = [];

    public int IcbariMatchedCount { get; set; }
    public int IcbariSentCount { get; set; }
    public double IcbariSentPct { get; set; }
    public List<IcbariRow> IcbariUnsent { get; set; } = [];

    public record ResourceBreakdownRow(string ResourceType, string Label, int Success, int Warning, int Danger)
    {
        public int Total => Success + Warning + Danger;
        public double SuccessPct => Total == 0 ? 0 : Math.Round(Success * 100.0 / Total, 1);
    }

    public record DeptVolumeRow(string Name, int Count, int Pct);
    public record NotReadyGroup(string Reason, int Count);
    public record ErrorCategoryGroup(string Label, string Description, int Count);
    public record IcbariRow(string PatientName, int ProtokolId, string ServiceName, string Reason);
    public record TrendLabel(double X, string Text, string Anchor);

    // Trend grafigi (SVG) sunucu tarafinda ONCEDEN hesaplanip cshtml'e hazir path/etiket
    // olarak veriliyor -- KULLANICI ISTEGI degil ama proje kurali: "no external CDN/font/
    // library" (site.css basligindaki not) ve mevcut XSS-guvenligi deseni (innerHTML/dinamik
    // veri yerine dogrudan Razor auto-escape) -- bkz. konusma. Mockup'taki client-side JS
    // versiyonu burada sunucu tarafina tasindi, boylece hasta/hata verisi hic JS'e gecmiyor.
    public string TrendSuccessPath { get; set; } = "";
    public string TrendFailedPath { get; set; } = "";
    public string TrendAreaPath { get; set; } = "";
    public List<TrendLabel> TrendLabels { get; set; } = [];
    public double TrendLastX { get; set; }
    public double TrendLastY { get; set; }

    public static string RelativeTime(DateTime createdAtUtc)
    {
        var span = DateTime.UtcNow - createdAtUtc;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} dk";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} sa";
        return $"{(int)span.TotalDays} gün";
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        (PeriodFromDate, PeriodToDate) = Donem switch
        {
            "0" => (today, today),
            "30" => (today.AddDays(-29), today),
            _ => (today.AddDays(-6), today),
        };
        PeriodFromUtc = PeriodFromDate.ToDateTime(TimeOnly.MinValue);
        PeriodToUtcExclusive = PeriodToDate.ToDateTime(TimeOnly.MinValue).AddDays(1);

        var todayFrom = today.ToDateTime(TimeOnly.MinValue);
        var todayToExclusive = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var yesterdayFrom = today.AddDays(-1).ToDateTime(TimeOnly.MinValue);

        var todayProtocols = await pusulaRepository.GetProtokolListAsync(todayFrom, todayToExclusive, null, ct);
        var yesterdayProtocols = await pusulaRepository.GetProtokolListAsync(yesterdayFrom, todayFrom, null, ct);
        TodayProtocolCount = todayProtocols.Count;
        TodayProtocolHasBaseline = yesterdayProtocols.Count > 0;
        TodayProtocolChangePct = yesterdayProtocols.Count == 0 ? 0 : Math.Round((TodayProtocolCount - yesterdayProtocols.Count) * 100.0 / yesterdayProtocols.Count, 1);

        var todayEncounterCounts = await syncLog.GetStatusCountsAsync("Encounter", todayFrom, todayToExclusive, ct);
        TodaySentCount = todayEncounterCounts.GetValueOrDefault(nameof(SyncStatus.Success));

        var periodCounts = await syncLog.GetStatusCountsAsync(null, PeriodFromUtc, PeriodToUtcExclusive, ct);
        var periodSuccess = periodCounts.GetValueOrDefault(nameof(SyncStatus.Success));
        var periodFailed = periodCounts.GetValueOrDefault(nameof(SyncStatus.Failed));
        FailedCount = periodFailed;
        PendingCount = periodCounts.GetValueOrDefault(nameof(SyncStatus.Skipped));
        OverallSuccessRatePct = (periodSuccess + periodFailed) == 0 ? 0 : Math.Round(periodSuccess * 100.0 / (periodSuccess + periodFailed), 1);

        var periodLengthDays = PeriodToDate.DayNumber - PeriodFromDate.DayNumber + 1;
        var prevFromUtc = PeriodFromUtc.AddDays(-periodLengthDays);
        var prevCounts = await syncLog.GetStatusCountsAsync(null, prevFromUtc, PeriodFromUtc, ct);
        var prevSuccess = prevCounts.GetValueOrDefault(nameof(SyncStatus.Success));
        var prevFailed = prevCounts.GetValueOrDefault(nameof(SyncStatus.Failed));
        var prevRate = (prevSuccess + prevFailed) == 0 ? 0 : prevSuccess * 100.0 / (prevSuccess + prevFailed);
        SuccessRateDeltaPct = Math.Round(OverallSuccessRatePct - prevRate, 1);

        var trendFrom = today.AddDays(-13).ToDateTime(TimeOnly.MinValue);
        var trendToExclusive = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
        Trend = await syncLog.GetDailyTrendAsync(trendFrom, trendToExclusive, ct);

        foreach (var rt in TrackedResourceTypes)
        {
            var counts = await syncLog.GetStatusCountsAsync(rt, PeriodFromUtc, PeriodToUtcExclusive, ct);
            var s = counts.GetValueOrDefault(nameof(SyncStatus.Success));
            var w = counts.GetValueOrDefault(nameof(SyncStatus.Skipped));
            var d = counts.GetValueOrDefault(nameof(SyncStatus.Failed));
            if (s + w + d > 0)
                Breakdown.Add(new ResourceBreakdownRow(rt, SyncLogEntry.ResourceTypeLabel(rt), s, w, d));
        }

        RecentErrors = await syncLog.QueryAsync("Failed", null, 6, 0, PeriodFromUtc, PeriodToUtcExclusive, ct);

        var periodProtocols = Donem == "0" ? todayProtocols : await pusulaRepository.GetProtokolListAsync(PeriodFromUtc, PeriodToUtcExclusive, null, ct);
        var deptGroups = periodProtocols
            .Where(p => !string.IsNullOrWhiteSpace(p.BolumAdi))
            .GroupBy(p => p.BolumAdi!)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .Take(6)
            .ToList();
        var maxDept = deptGroups.Count == 0 ? 0 : deptGroups[0].Count;
        DeptVolume = deptGroups
            .Select(g => new DeptVolumeRow(g.Name, g.Count, maxDept == 0 ? 0 : (int)Math.Round(g.Count * 100.0 / maxDept)))
            .ToList();

        var notReadyEntries = await syncLog.QueryAsync("Skipped", "Composition", 500, 0, PeriodFromUtc, PeriodToUtcExclusive, ct);
        NotReadyGroups = notReadyEntries
            .GroupBy(e => e.Message ?? "Belirtilmemiş")
            .Select(g => new NotReadyGroup(g.Key, g.Count()))
            .OrderByDescending(g => g.Count)
            .Take(6)
            .ToList();

        var failedEntries = await syncLog.QueryAsync("Failed", null, 500, 0, PeriodFromUtc, PeriodToUtcExclusive, ct);
        ErrorCategories = failedEntries
            .Select(e => SyncLogEntry.ErrorCategory(e.Message))
            .GroupBy(c => c.Label)
            .Select(g => new ErrorCategoryGroup(g.Key, g.First().Description, g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();

        var icbariIslemler = await pusulaRepository.GetIcbariIslemlerAsync(PeriodFromUtc, PeriodToUtcExclusive, ct);
        IcbariMatchedCount = icbariIslemler.Count;
        var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", icbariIslemler.Select(i => i.IslemId).ToList(), ct);
        IcbariSentCount = icbariIslemler.Count(i => procedureStatuses.GetValueOrDefault(i.IslemId) is { Status: SyncStatus.Success });
        IcbariSentPct = IcbariMatchedCount == 0 ? 0 : Math.Round(IcbariSentCount * 100.0 / IcbariMatchedCount, 1);
        IcbariUnsent = icbariIslemler
            .Where(i => procedureStatuses.GetValueOrDefault(i.IslemId) is not { Status: SyncStatus.Success })
            .Select(i =>
            {
                var latest = procedureStatuses.GetValueOrDefault(i.IslemId);
                var reason = latest is { Status: SyncStatus.Failed } ? (latest.Message ?? "Hata") : "Henüz gönderilmedi";
                return new IcbariRow(string.IsNullOrWhiteSpace(i.PatientName) ? "-" : i.PatientName, i.ProtokolId, i.HizmetAdi ?? i.IcbariAdi, reason);
            })
            .Take(20)
            .ToList();

        BuildTrendGeometry();
    }

    private void BuildTrendGeometry()
    {
        const double w = 720, h = 220, padL = 8, padR = 8, padT = 10, padB = 24;
        var n = Trend.Count;
        if (n < 2) return;

        var maxV = Math.Max(1, Trend.Max(t => t.Success)) * 1.15;
        var stepX = (w - padL - padR) / (n - 1);
        double X(int i) => padL + i * stepX;
        double Y(int v) => h - padB - v / maxV * (h - padT - padB);
        static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        string PathFor(Func<int, int> selector) =>
            string.Join(" ", Enumerable.Range(0, n).Select(i => $"{(i == 0 ? "M" : "L")}{F(X(i))},{F(Y(selector(i)))}"));

        TrendSuccessPath = PathFor(i => Trend[i].Success);
        TrendFailedPath = PathFor(i => Trend[i].Failed);
        TrendAreaPath = $"{TrendSuccessPath} L{F(X(n - 1))},{F(h - padB)} L{F(X(0))},{F(h - padB)} Z";
        TrendLastX = X(n - 1);
        TrendLastY = Y(Trend[n - 1].Success);

        var labelIdx = new[] { 0, n / 2, n - 1 }.Distinct();
        TrendLabels = labelIdx
            .Select(i => new TrendLabel(X(i), Trend[i].Day.ToString("dd.MM"), i == 0 ? "start" : i == n - 1 ? "end" : "middle"))
            .ToList();
    }
}
