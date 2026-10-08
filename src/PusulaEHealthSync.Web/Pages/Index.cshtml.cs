using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// Protokol Listesi -- ana ekran (KARAR: 2026-08-19, bkz. web-ia-plan artifact bolum 04).
// Pusula'nin kendi ENabiz Gonderim ekranindaki mantik (protokol satiri + durum filtreleri)
// esas alindi.
//
// ROZETLER (2026-10-08): Hasta ve Muayene'nin yani sira Epikriz, Laboratuvar, Radyoloji ve
// Patoloji de gosteriliyor. Oncesinde Lab ve Epikriz rozetleri ekranda SABIT "Henüz
// gelistirilmedi" metniydi ve Rad rozeti hic yoktu -- modüller Agustos/Eylul'de yazilmis
// ama listedeki gostergeleri hic baglanmamisti. Kullanici hakli olarak sordu: "radyoloji
// ya da laboratuvar gonderilse bile yesil olmuyor."
public class IndexModel(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    SettingsStore settings,
    ProtocolFullSyncService protocolFullSync,
    DeleteService deleteService,
    ILogger<IndexModel> logger) : PageModel
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
        // Epikriz / Laboratuvar / Radyoloji / Patoloji rozetleri. Anahtar ResourceType.
        // YALNIZCA GOSTERILEN SAYFA icin doldurulur (bkz. RozetleriDoldurAsync).
        public Dictionary<string, GonderimRozeti> Rozetler { get; init; } = [];

        // "Hatali olanlari sec" toplu-secim butonu icin -- Hasta VEYA Muayine son
        // denemesi Failed ise bu protokol "hatali" sayilir.
        public bool Hatali => HastaDurumKaydi?.Status == SyncStatus.Failed || MuayineDurumKaydi?.Status == SyncStatus.Failed;
    }

    // Bir protokolun TEK BIR kayit turundeki gonderim durumu (Lab, Rad, Epikriz, Patoloji).
    //
    // NEDEN AYRI BIR TIP: Hasta ve Muayine'de protokol basina TEK kayit var, o yuzden
    // SyncLogEntry'nin kendisi yetiyordu. Laboratuvarda ise bir protokolde 70 satir
    // olabiliyor; rozet onlarin TOPLU halini anlatmali.
    //
    // EN KOTU DURUM KAZANIR -- Aktivite Akisi'ndaki hasta siniflandirmasiyla ayni kural:
    // bir satir hata aldiysa rozet kirmizi. Yesil rozet "hepsi temiz" demek olmali, yoksa
    // kullanici ekrana guvenip hatayi kacirir.
    public record GonderimRozeti(int PusulaKayit, int Basarili, int Atlanan, int Hatali)
    {
        public int GonderimKaydi => Basarili + Atlanan + Hatali;

        // GONDERIM KAYDI SAYISI ILE PUSULA SATIR SAYISI KASITLI OLARAK KARSILASTIRILMIYOR.
        //
        // Laboratuvarda bakanlik istegi geregi bir PANEL tek Observation olarak gidiyor
        // (bkz. LabGroupBuilder): 28 satirlik bir hemogram icin tek gonderim yapiliyor.
        // Uye satirlarin da SyncLog kaydi almasi 2026-10-05'te eklendi, yani ONCESINDE
        // gonderilmis protokollerde 28 satira karsilik 1 kayit var. "Kayit sayisi satir
        // sayisindan az ise eksik" deseydik, dogru gonderilmis her eski protokol ekranda
        // kirmizi/sari gorunurdu. Rozet bu yuzden VAR OLAN kayitlarin en kotusunu
        // gosteriyor; iki sayi da ipucu metninde yaziyor, isteyen bakar.
        public string Durum =>
            Hatali > 0 ? "danger" :
            Atlanan > 0 ? "warning" :
            Basarili > 0 ? "success" : "pending";

        public string Etiket =>
            Hatali > 0 ? $"{Hatali} hatalı" :
            Atlanan > 0 ? "Eksik veri" :
            Basarili > 0 ? "Gönderildi" : "Gönderilmedi";

        public string Ipucu => GonderimKaydi == 0
            ? $"Pusula'da {PusulaKayit} gönderilmeye uygun kayıt var, henüz hiçbiri için gönderim denemesi yok."
            : $"Pusula'da {PusulaKayit} kayıt · gönderim denemesi: {Basarili} başarılı, {Atlanan} eksik veri, {Hatali} hatalı.";
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

        await RozetleriDoldurAsync(ct);
    }

    // EPIKRIZ / LAB / RADYOLOJI / PATOLOJI ROZETLERI (2026-10-08).
    //
    // YALNIZCA GOSTERILEN SAYFA ICIN (30 protokol): bu dort tur, Hasta ve Muayine'den
    // farkli olarak protokolden dogrudan okunamiyor -- SyncLog cocuk kaydin id'siyle
    // tutuluyor, o yuzden once Pusula'dan "bu protokolde hangi laboratuvar satirlari
    // var" diye sormak gerekiyor. Tarih araligindaki TUM adaylar icin yapilsaydi
    // (binlerce protokol, on binlerce laboratuvar satiri) liste ekrani kullanilamaz
    // hale gelirdi. Sayfadaki 30 protokol icin olculdu: dort sorgu toplam ~0,7 sn.
    //
    // HER TUR KENDI BASINA DAYANIKLI: biri patlarsa yalnizca o rozet eksik kalir,
    // liste yine acilir. Protokol listesi, Pusula'nin yarisi calismazken bile
    // gosterilebilmeli.
    private async Task RozetleriDoldurAsync(CancellationToken ct)
    {
        if (Rows.Count == 0) return;
        var protokolIds = Rows.Select(r => r.Protokol.ProtokolId).Distinct().ToList();

        foreach (var tip in RozetTurleri)
        {
            try
            {
                var cocuklar = await pusulaRepository.ProtokolCocukIdleriAsync(tip, protokolIds, ct);
                if (cocuklar.Count == 0) continue;

                // Tum sayfanin cocuk id'leri TEK sorguda -- protokol basina ayri sorgu
                // 30 SQLite gidis-donusu olurdu.
                var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
                    tip, cocuklar.Values.SelectMany(x => x).Distinct().ToList(), ct, govdeleriGetir: false);

                foreach (var row in Rows)
                {
                    if (!cocuklar.TryGetValue(row.Protokol.ProtokolId, out var idler)) continue;
                    var kayitlar = idler.Select(durumlar.GetValueOrDefault).OfType<SyncLogEntry>().ToList();
                    row.Rozetler[tip] = new GonderimRozeti(
                        idler.Count,
                        kayitlar.Count(k => k.Status == SyncStatus.Success),
                        kayitlar.Count(k => k.Status == SyncStatus.Skipped),
                        kayitlar.Count(k => k.Status == SyncStatus.Failed));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Protokol listesi: {Tip} rozetleri okunamadi.", tip);
            }
        }
    }

    // Rozet sirasi ekrandaki sirayla ayni tutuluyor.
    public static readonly string[] RozetTurleri =
        ["Composition", "Observation", "DiagnosticReport", "DiagnosticReport-Patoloji"];

    public static string RozetBasligi(string resourceType) => resourceType switch
    {
        "Composition" => "Epikriz",
        "Observation" => "Lab",
        "DiagnosticReport" => "Rad",
        "DiagnosticReport-Patoloji" => "Patoloji",
        _ => resourceType,
    };

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
    // canli (liveMode:true) cagrilir; hasta TRƏS'te yoksa o da otomatik once
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

            // DUZELTME (2026-09-15, kullanici bildirdi): eskiden burada SADECE
            // encounterSyncService.SyncOneAsync cagriliyordu -- Epikriz, Laboratuvar ve
            // Patoloji HIC gonderilmiyordu. Sunucuda bu yuzden 3 protokolde 222 onayli
            // laboratuvar sonucu hic denenmeden kaldi. Artik Protokol Detay'daki
            // "Tümünü Gönder" ile AYNI servisi cagiriyor (bkz. ProtocolFullSyncService).
            var result = await protocolFullSync.SyncAllAsync(protokolId, ct);
            switch (result.EncounterStatus)
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

    // Pusula'da State=0'a dusmus (iptal/silinmis) ama TRƏS'te hala kayitli gorunen
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
            : $"{selectedLogIds.Count} iptal edilmiş protokolün TRƏS kaydı silinmeye çalışıldı -- {ok} silindi, {failed} hata.";

        return RedirectToPage("/Index", new { From, To, Search, HastaDurumu, ProtokolDurumu, IcbariSadece, P });
    }
}
