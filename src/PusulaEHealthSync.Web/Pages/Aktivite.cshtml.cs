using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
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
//
// PROTOKOLE GORE GRUPLAMA (KULLANICI ISTEGI 2026-10-05): "gonderim akisinda tek tek tum
// islemler ve gonderimler icin tek tek satir yapmasin, protokole gore gruplayip yapsin,
// protokol bilgisi vs gosterilsin."
//
// Tek bir protokolun gonderimi onlarca satir uretiyor (bir hemogram paneli 50 satir
// olabiliyor); duz liste halinde neyin hangi hastaya ait oldugu kayboluyordu.
public class AktiviteModel(SyncLogStore syncLog, PusulaRepository repository,
    ILogger<AktiviteModel> logger) : PageModel
{
    public Dictionary<string, int> StatusCounts { get; set; } = new();
    public List<SyncLogEntry> Entries { get; set; } = [];
    public List<AktiviteGrubu> Gruplar { get; set; } = [];
    public int PageNumber { get; set; }
    public bool HasNextPage { get; set; }

    // GRUPLAMA SAYFA BOYUTUNU BUYUTTU: 25 kayit, tek bir protokolun laboratuvari bile
    // olabiliyor -- gruplu gorunumde sayfa basina tek grup dusmesi anlamsiz olurdu.
    private const int PageSize = 60;

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

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? today.AddDays(-6);
        EffectiveTo = To ?? today;
        PageNumber = P < 1 ? 1 : P;

        // Filtredeki tarihler YEREL gun (kullanici takvimden secer), SyncLog ise UTC saklar --
        // cevrim sart. Eskiden yerel gece yarisi dogrudan UTC alaniyla karsilastiriliyordu,
        // bu da gun sinirini fiilen 04:00'e kaydiriyordu (bkz. AzTime).
        var fromUtc = AzTime.ToUtc(EffectiveFrom.ToDateTime(TimeOnly.MinValue));
        var toUtcExclusive = AzTime.ToUtc(EffectiveTo.ToDateTime(TimeOnly.MinValue).AddDays(1));

        StatusCounts = await syncLog.GetStatusCountsAsync(ResourceType, fromUtc, toUtcExclusive, ct);

        if (Status == "Failed" && !string.IsNullOrWhiteSpace(Kategori))
        {
            var allFailed = await syncLog.QueryAsync("Failed", ResourceType, 2000, 0, fromUtc, toUtcExclusive, ct);
            var filtered = allFailed.Where(e => SyncLogEntry.ErrorCategory(e.Message).Label == Kategori).ToList();
            HasNextPage = filtered.Count > PageNumber * PageSize;
            Entries = filtered.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        }
        else
        {
            var take = PageSize + 1; // bir fazla cekip "sonraki sayfa var mi" anlamak icin
            var rows = await syncLog.QueryAsync(Status, ResourceType, take, (PageNumber - 1) * PageSize, fromUtc, toUtcExclusive, ct);
            HasNextPage = rows.Count > PageSize;
            Entries = rows.Take(PageSize).ToList();
        }

        Gruplar = await GruplaAsync(Entries, ct);
    }

    // SyncLog'da protokol bagi YOK -- her kaynak tipi kendi id uzayini kullaniyor. Protokol
    // numarasini Pusula'ya sorarak cozuyoruz (bkz. PusulaRepository.ProtokolIdleriniCozAsync,
    // ayni yontem gun sonu raporunda da kullaniliyor).
    //
    // PUSULA ERISILEMEZSE SAYFA YINE ACILIR: bu sayfa bugune kadar HIC Pusula'ya
    // baglanmiyordu, yalnizca SyncLog okuyordu. Gruplama ugruna "Pusula yoksa senkron
    // gunlugu de yok" durumuna dusmek yanlis olurdu -- gunluk, tam da Pusula'ya
    // erisilemedigi anda en cok ihtiyac duyulan sey. Cozum basarisiz olursa tum kayitlar
    // tek bir "protokolu cozulemeyen" grubunda gorunur.
    private async Task<List<AktiviteGrubu>> GruplaAsync(List<SyncLogEntry> kayitlar, CancellationToken ct)
    {
        if (kayitlar.Count == 0) return [];

        var esleme = new Dictionary<(string, int), int>();
        foreach (var grup in kayitlar.GroupBy(k => k.ResourceType))
        {
            try
            {
                var cozum = await repository.ProtokolIdleriniCozAsync(
                    grup.Key, grup.Select(k => k.PusulaId).Distinct().ToList(), ct);
                foreach (var kv in cozum) esleme[(grup.Key, kv.Key)] = kv.Value;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Aktivite akisi: {Tip} icin protokol numaralari cozulemedi.", grup.Key);
            }
        }

        var protokoller = new Dictionary<int, ProtokolListItem>();
        if (esleme.Count > 0)
        {
            try
            {
                protokoller = await repository.GetProtokollerByIdsAsync(
                    esleme.Values.Distinct().ToList(), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Aktivite akisi: protokol bilgileri okunamadi.");
            }
        }

        // SIRA KORUNUYOR: akis en yeniden eskiye okunuyor, gruplar da ilk gorundukleri
        // sirayla diziliyor. Grup icindeki kayitlar da geldigi sirayi koruyor.
        var sonuc = new List<AktiviteGrubu>();
        var indeks = new Dictionary<int, AktiviteGrubu>();
        AktiviteGrubu? protokolsuz = null;   // ayri tutuluyor: Dictionary anahtari null olamaz

        foreach (var k in kayitlar)
        {
            if (esleme.TryGetValue((k.ResourceType, k.PusulaId), out var pid))
            {
                if (!indeks.TryGetValue(pid, out var grup))
                {
                    grup = new AktiviteGrubu(pid, protokoller.GetValueOrDefault(pid), []);
                    indeks[pid] = grup;
                    sonuc.Add(grup);
                }
                grup.Kayitlar.Add(k);
            }
            else
            {
                if (protokolsuz is null)
                {
                    protokolsuz = new AktiviteGrubu(null, null, []);
                    sonuc.Add(protokolsuz);
                }
                protokolsuz.Kayitlar.Add(k);
            }
        }

        // Protokolsuz grup (Hasta / Doktor kayitlari -- protokole ait degiller) en sona.
        return sonuc.OrderBy(g => g.ProtokolId is null ? 1 : 0).ToList();
    }

    public int TotalCount => StatusCounts.Values.Sum();
    public int SuccessCount => StatusCounts.GetValueOrDefault(nameof(SyncStatus.Success));
    public int SkippedCount => StatusCounts.GetValueOrDefault(nameof(SyncStatus.Skipped));
    public int FailedCount => StatusCounts.GetValueOrDefault(nameof(SyncStatus.Failed));
}

// Aktivite akisinda tek bir protokolun o sayfadaki kayitlari.
// ProtokolId null ise kayitlar protokole ait degil (Hasta, Doktor) ya da protokol
// numarasi cozulemedi.
public record AktiviteGrubu(int? ProtokolId, ProtokolListItem? Protokol, List<SyncLogEntry> Kayitlar)
{
    public int Basarili => Kayitlar.Count(k => k.Status == SyncStatus.Success);
    public int Hatali => Kayitlar.Count(k => k.Status == SyncStatus.Failed);
    public int Atlanan => Kayitlar.Count(k => k.Status == SyncStatus.Skipped);

    // Grup basligindaki saat -- gruptaki EN YENI kayit (akis en yeniden eskiye okunuyor).
    public DateTime SonZamanYerel => AzTime.ToLocal(Kayitlar.Max(k => k.CreatedAtUtc));

    // Protokol bilgisi okunamadiysa bile kayitlardaki hasta adi gosterilebilir.
    public string? HastaAdi => Protokol?.HastaAdiSoyadi is { Length: > 0 } ad
        ? ad
        : Kayitlar.Select(k => k.PatientFullName).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

    public string? Fin => Protokol?.Fin is { Length: > 0 } f
        ? f
        : Kayitlar.Select(k => k.Fin).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
}
