using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// Protokol Listesi -- ana ekran (KARAR: 2026-08-19, bkz. web-ia-plan artifact bolum 04).
// Pusula'nin kendi ENabiz Gonderim ekranindaki mantik (protokol satiri + durum filtreleri)
// esas alindi. Patient ve Encounter senkron ediliyor (2026-08-20: Organization/5204 cevabiyla
// Encounter da acildi); Condition/Lab/Epikriz kod tarafinda henuz yok, bu yuzden o kolonlar
// tabloda "Hazir degil" olarak sabit gosterilir -- yanlis izlenim vermesin diye.
public class IndexModel(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    SettingsStore settings,
    EncounterSyncService encounterSyncService,
    DeleteService deleteService) : PageModel
{
    private const int PageSize = 30;

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string HastaDurumu { get; set; } = "Tumu";

    // Pusula'daki acik/kapali durumu (KapanisTarihi) -- sync-log'a bagli degil, saf
    // protokol verisinden filtrelenir. "Kapanis bekleyenleri" izlemek icin (bkz. konusma).
    [BindProperty(SupportsGet = true)]
    public string ProtokolDurumu { get; set; } = "Tumu";

    // KULLANICI ISTEGI (2026-08-29): "icbari hastaları diye bir checkbox ekleyelim,
    // işaretlendiğinde kurumu icbari olanlar listelensin" -- Genel Bakış'taki İcbari
    // Sigorta bölümüyle AYNI eslesme kurali (GetIcbariProtokolIdsAsync, bkz. o metot).
    [BindProperty(SupportsGet = true)]
    public bool IcbariSadece { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }
    public bool SearchActive { get; set; }
    public int PageNumber { get; set; }
    public bool HasNextPage { get; set; }
    public int OpenProtokolSendAfterDays { get; set; }

    // "Gonderilmis ama Pusula'da sonradan iptal/silinmis (State=0)" mutabakati -- ayri,
    // uyari renkli bir panelde gosterilir; toplu silme icin OnPostBulkSilIptalAsync kullanir.
    public List<SyncLogEntry> VoidedButSentEntries { get; set; } = [];

    public List<ProtokolRow> Rows { get; set; } = [];
    public int CountTumu { get; set; }
    public int CountGonderildi { get; set; }
    public int CountGonderilmedi { get; set; }
    public int CountAcik { get; set; }
    public int CountKapali { get; set; }

    [TempData]
    public string? BulkResultMessage { get; set; }

    public record ProtokolRow(ProtokolListItem Protokol, SyncLogEntry? HastaDurumKaydi, SyncLogEntry? MuayineDurumKaydi, bool MuayineGonderimeUygun)
    {
        // "Hatali olanlari sec" toplu-secim butonu icin -- Hasta VEYA Muayine son
        // denemesi Failed ise bu protokol "hatali" sayilir.
        public bool Hatali => HastaDurumKaydi?.Status == SyncStatus.Failed || MuayineDurumKaydi?.Status == SyncStatus.Failed;
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? today.AddDays(-6);
        EffectiveTo = To ?? today;
        SearchActive = !string.IsNullOrWhiteSpace(Search);
        PageNumber = P < 1 ? 1 : P;
        OpenProtokolSendAfterDays = await settings.GetIntAsync(
            SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);

        // Arama varsa tarih araligi tamamen yok sayilir (bkz. GetProtokolListAsync) --
        // FIN/isim/protokol Id ile arama yapan biri, o kayit hangi tarihte olursa olsun
        // bulabilmeli.
        var candidates = await pusulaRepository.GetProtokolListAsync(
            EffectiveFrom.ToDateTime(TimeOnly.MinValue),
            EffectiveTo.ToDateTime(TimeOnly.MinValue).AddDays(1),
            string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
            ct);

        await LoadVoidedButSentAsync(ct);

        var hastaIds = candidates.Select(c => c.HastaId).Distinct().ToList();
        var patientStatuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", hastaIds, ct);

        var protokolIds = candidates.Select(c => c.ProtokolId).Distinct().ToList();
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", protokolIds, ct);

        var withStatus = candidates
            .Select(c => new ProtokolRow(
                c,
                patientStatuses.GetValueOrDefault(c.HastaId),
                encounterStatuses.GetValueOrDefault(c.ProtokolId),
                EncounterMapper.IsEligibleForSend(c.AcilisTarihi, c.KapanisTarihi, OpenProtokolSendAfterDays)))
            .ToList();

        CountTumu = withStatus.Count;
        CountGonderildi = withStatus.Count(r => r.HastaDurumKaydi?.Status == SyncStatus.Success);
        CountGonderilmedi = CountTumu - CountGonderildi;
        CountKapali = withStatus.Count(r => r.Protokol.KapanisTarihi is not null);
        CountAcik = CountTumu - CountKapali;

        IEnumerable<ProtokolRow> filtered = HastaDurumu switch
        {
            "Gonderildi" => withStatus.Where(r => r.HastaDurumKaydi?.Status == SyncStatus.Success),
            "Gonderilmedi" => withStatus.Where(r => r.HastaDurumKaydi?.Status != SyncStatus.Success),
            _ => withStatus,
        };
        filtered = ProtokolDurumu switch
        {
            "Acik" => filtered.Where(r => r.Protokol.KapanisTarihi is null),
            "Kapali" => filtered.Where(r => r.Protokol.KapanisTarihi is not null),
            _ => filtered,
        };
        var filteredList = filtered.ToList();

        if (IcbariSadece)
        {
            var icbariProtokolIds = await pusulaRepository.GetIcbariProtokolIdsAsync(
                filteredList.Select(r => r.Protokol.ProtokolId).Distinct().ToList(), ct);
            filteredList = filteredList.Where(r => icbariProtokolIds.Contains(r.Protokol.ProtokolId)).ToList();
        }

        HasNextPage = filteredList.Count > PageNumber * PageSize;
        Rows = filteredList.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
    }

    public static string GelisTipiLabel(string? code) => code switch
    {
        "A" => "Ayaktan",
        "Y" => "Yatan",
        "G" => "Günübirlik",
        null => "-",
        _ => code,
    };

    // Isimler Hasta.ProtokolTipi tablosundan (KULLANICI ISTEGI, 2026-08-21) -- bkz.
    // EncounterMapper.ProtokolTipiDisplay. Listede olmayan (yeni eklenmis/nadir) bir Id
    // gelirse ham numara gosterilir, tahmin uretilmez.
    public static string ProtokolTipiLabel(byte? id) => id is null
        ? "-"
        : EncounterMapper.ProtokolTipiDisplay.GetValueOrDefault(id.Value, id.Value.ToString());

    // Toplu "Seçilenleri Gönder" -- her protokol icin EncounterSyncService.SyncOneAsync
    // canli (liveMode:true) cagrilir; hasta e-Health'te yoksa o da otomatik once
    // gonderilir (bkz. EncounterSyncService). Filtre/sayfa durumu korunarak Index'e doner.
    // KARAR (2026-08-20, kullanici istegi -- canli testte 30 protokol art arda iki kez
    // gonderilince ortaya cikti): zaten BASARIYLA gonderilmis (AzResourceId dolu, en son
    // islem Delete degil) protokoller toplu gonderimde ATLANIR -- tekrar Update atilmaz.
    // Bilerek TEKRAR gondermek isteyen kullanici, Protokol Detay'daki "Tekrar Gönder"
    // butonunu (tek kayit, acikca istenen bir islem) kullanmaya devam edebilir -- bu
    // atlama SADECE toplu secimde gecerli.
    public async Task<IActionResult> OnPostBulkGonderAsync(List<int> selectedProtokolIds, CancellationToken ct)
    {
        var distinctIds = selectedProtokolIds.Distinct().ToList();
        var existingStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", distinctIds, ct);

        int ok = 0, skipped = 0, failed = 0, alreadySent = 0;
        foreach (var protokolId in distinctIds)
        {
            var existing = existingStatuses.GetValueOrDefault(protokolId);
            if (existing is { Status: SyncStatus.Success, AzResourceId: not null } && existing.Operation != SyncOperation.Delete)
            {
                alreadySent++;
                continue;
            }

            var result = await encounterSyncService.SyncOneAsync(protokolId, liveMode: true, ct);
            switch (result.Status)
            {
                case SyncStatus.Success: ok++; break;
                case SyncStatus.Skipped: skipped++; break;
                case SyncStatus.Failed: failed++; break;
            }
        }

        BulkResultMessage = selectedProtokolIds.Count == 0
            ? "Hiçbir protokol seçilmedi."
            : $"{selectedProtokolIds.Count} protokol işlendi -- {ok} gönderildi, {alreadySent} zaten gönderilmişti (atlandı), {skipped} atlandı, {failed} hata.";

        return RedirectToPage("/Index", new { From, To, Search, HastaDurumu, ProtokolDurumu, IcbariSadece, P });
    }

    // Pusula'da State=0'a dusmus (iptal/silinmis) ama e-Health'te hala kayitli gorunen
    // Encounter'lari bulur (bkz. konusma, 2026-08-20: "gonderimi yapilan bir protokol
    // silinirse gonderimler de silinsin"). Otomatik/sessiz silmiyoruz -- kullaniciya
    // ayri bir uyari panelinde gosterip, tek onayla toplu silme sunuyoruz (mevcut Sil
    // ozelligiyle ayni guvenlik yaklasimi: her zaman gorunur ve geri donusu olmayan bir
    // islem oldugu icin acikca tetiklenmeli).
    private async Task LoadVoidedButSentAsync(CancellationToken ct)
    {
        var sentEntries = await syncLog.GetActiveSentEncounterEntriesAsync(ct);
        if (sentEntries.Count == 0) return;

        var states = await pusulaRepository.GetStatesByIdsAsync(sentEntries.Select(e => e.PusulaId).Distinct().ToList(), ct);
        VoidedButSentEntries = sentEntries
            .Where(e => states.TryGetValue(e.PusulaId, out var state) && state == 0)
            .OrderByDescending(e => e.Id)
            .ToList();
    }

    public async Task<IActionResult> OnPostBulkSilIptalAsync(List<long> selectedLogIds, CancellationToken ct)
    {
        int ok = 0, failed = 0;
        foreach (var logId in selectedLogIds.Distinct())
        {
            var entry = await syncLog.GetByIdAsync(logId, ct);
            if (entry is null) continue;

            var result = await deleteService.DeleteAsync(entry, ct);
            if (result.Status == SyncStatus.Success) ok++; else failed++;
        }

        BulkResultMessage = selectedLogIds.Count == 0
            ? "Hiçbir kayıt seçilmedi."
            : $"{selectedLogIds.Count} iptal edilmiş protokolün e-Health kaydı silinmeye çalışıldı -- {ok} silindi, {failed} hata.";

        return RedirectToPage("/Index", new { From, To, Search, HastaDurumu, ProtokolDurumu, IcbariSadece, P });
    }
}
