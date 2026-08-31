using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Teknik aktivite akisi -- her senkron denemesinin ham kaydi (SyncLog). Protokol
// Listesi ana ekran olduktan sonra bu sayfa ikincil katman: "gonderilen her seyin
// duz listesi" gerektiginde (ozellikle Patient disi kayit turleri eklendikce) burasi
// kullanilir.
//
// KULLANICI ISTEGI (2026-08-25): "ust bardaki sayilarin ustune tarih koyalim, tarihe
// gore listelensin, bilgi bari da secim yapilabilir olsun" -- Index.cshtml'deki From/To
// tarih araligi deseni + Doktorlar/BolumEslestirme'deki tiklanabilir stat deseni buraya
// da uygulandi.
public class AktiviteModel(SyncLogStore syncLog) : PageModel
{
    public Dictionary<string, int> StatusCounts { get; set; } = new();
    public List<SyncLogEntry> Entries { get; set; } = [];
    public int PageNumber { get; set; }
    public bool HasNextPage { get; set; }
    private const int PageSize = 25;

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ResourceType { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    // Genel Bakış'taki "Hata Kategorileri" kutucuklarından geliyor -- SyncLogEntry.ErrorCategory
    // ile ayni etiketle eslesen Failed kayitlarini gosterir. Bu bir DB sutunu degil (mesaj
    // metninden turetiliyor), o yuzden SQL'de degil, gecici olarak genis bir Failed kumesi
    // cekilip burada bellek icinde filtreleniyor -- GenelBakis'teki ayni yaklasimin devami.
    [BindProperty(SupportsGet = true)]
    public string? Kategori { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }

    public async Task OnGetAsync()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? today.AddDays(-6);
        EffectiveTo = To ?? today;
        PageNumber = P < 1 ? 1 : P;

        var fromUtc = EffectiveFrom.ToDateTime(TimeOnly.MinValue);
        var toUtcExclusive = EffectiveTo.ToDateTime(TimeOnly.MinValue).AddDays(1);

        StatusCounts = await syncLog.GetStatusCountsAsync(ResourceType, fromUtc, toUtcExclusive);

        if (Status == "Failed" && !string.IsNullOrWhiteSpace(Kategori))
        {
            var allFailed = await syncLog.QueryAsync("Failed", ResourceType, 2000, 0, fromUtc, toUtcExclusive);
            var filtered = allFailed.Where(e => SyncLogEntry.ErrorCategory(e.Message).Label == Kategori).ToList();
            HasNextPage = filtered.Count > PageNumber * PageSize;
            Entries = filtered.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        }
        else
        {
            var take = PageSize + 1; // bir fazla cekip "sonraki sayfa var mi" anlamak icin
            var rows = await syncLog.QueryAsync(Status, ResourceType, take, (PageNumber - 1) * PageSize, fromUtc, toUtcExclusive);
            HasNextPage = rows.Count > PageSize;
            Entries = rows.Take(PageSize).ToList();
        }
    }

    public int TotalCount => StatusCounts.Values.Sum();
    public int SuccessCount => StatusCounts.GetValueOrDefault(nameof(SyncStatus.Success));
    public int SkippedCount => StatusCounts.GetValueOrDefault(nameof(SyncStatus.Skipped));
    public int FailedCount => StatusCounts.GetValueOrDefault(nameof(SyncStatus.Failed));
}
