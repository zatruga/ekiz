using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// KULLANICI ISTEGI (2026-08-24): Doktor (Practitioner) takibi/gonderimi bu sayfadan
// KALDIRILDI -- "protokol detayda olmasin, onun takibini yapmayalim, sistem tarafinda
// doktorlar diye ayri bir yer olsun". Doktor artik sadece EncounterSyncService'in
// otomatik cascade'i (bkz. o dosyadaki KARAR notu) ve /Doktorlar sayfasi uzerinden
// yonetiliyor.
public class ProtokolModel(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    SettingsStore settings,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    CompositionSyncService compositionSyncService,
    ConditionSyncService conditionSyncService,
    ProcedureSyncService procedureSyncService,
    LabResultSyncService labResultSyncService,
    RadiologyReportSyncService radiologyReportSyncService,
    PathologyReportSyncService pathologyReportSyncService,
    DeleteService deleteService,
    CancellationSyncService cancellationSyncService,
    ProtocolFullSyncService protocolFullSync,
    ILogger<ProtokolModel> logger,
    EHealthClient eHealthClient) : PageModel
{
    public ProtokolListItem? Protokol { get; set; }
    public HastaRecord? Hasta { get; set; }
    public GenelMuayeneRecord? GenelMuayene { get; set; }
    public SyncLogEntry? HastaDurumKaydi { get; set; }
    public SyncLogEntry? MuayineDurumKaydi { get; set; }
    public SyncLogEntry? EpikrizDurumKaydi { get; set; }
    public List<(IcdTaniRecord Tani, SyncLogEntry? Durum)> Tanilar { get; set; } = [];
    public List<(IslemRecord Islem, SyncLogEntry? Durum)> Islemler { get; set; } = [];

    // Laboratuvar (Tetkik) -- KULLANICI ISTEGI (2026-08-29): "tetkik kısmına başlayalım,
    // laboratuvar önce". GetLabResultsByProtokolIdAsync sadece Status=6 (onaylanmis/kesinlesmis)
    // sonuclari donduruyor, procedure-code eslesmesi olmayanlar Skipped (bkz. LabResultObservationMapper).
    public List<(LabResultRecord Lab, SyncLogEntry? Durum)> Labs { get; set; } = [];
    public bool LabsGonderilebilir => Labs.Any(l => !BasariylaGonderildi(l.Durum));
    public bool LabsSilinebilir => Labs.Any(l => SyncLogEntry.CanDelete(l.Durum));

    // KULLANICI ISTEGI (2026-08-31): "sağ tarafta ... çok ucsuz bucaksız uzayıp gidiyor"
    // -- Tanı/İşlem/Laboratuvar artik sag sutunda degil, checklist satirina tiklayinca
    // sayfa altinda ortak bir alanda goruntuleniyor (bkz. Protokol.cshtml, pusulaShowTab).
    // Bu satirdaki tek rozet, LabGroup.AggregateBadge() ile AYNI mantik (en dikkat cekici
    // duruma gore ozetleniyor) ama TUM Labs listesi uzerinden.
    public (string CssClass, string Label) LabsAggregateBadge()
    {
        if (Labs.Count == 0) return ("neutral", "Gönderilmedi");
        if (Labs.Any(x => x.Durum?.Status == SyncStatus.Failed)) return ("danger", "Hatalı");
        if (Labs.All(x => BasariylaGonderildi(x.Durum))) return ("success", "Gönderildi");
        if (Labs.Any(x => BasariylaGonderildi(x.Durum))) return ("warning", "Kısmen gönderildi");
        return ("neutral", "Gönderilmedi");
    }

    // KULLANICI ISTEGI (2026-08-31): "ana başlıkta alt gönderim bilgisini yazalım örnek
    // tanı 2/2 başarılı, işlem 10/6 kısmi gibi diğer alt başlığı olan tüm alanlar için
    // yapalım" -- her alt listenin (Tanı/İşlem/Laboratuvar) VE bunlari toplayan "Müayinə
    // → Tanı & İşlem" ust basliginin yanina kac tanesinin basariyla gonderildigini
    // (X/Toplam + durum kelimesi) gosteren kucuk bir ozet. AggregateBadge()'lerle (yukarida,
    // LabGroup'ta) AYNI oncelik sirasi (Hatali > Basarili > hicbiri > Kismi) ama sayisal.
    public (string CssClass, string Label, int Success, int Total) Ozet(IEnumerable<SyncLogEntry?> durumlar)
    {
        var list = durumlar.ToList();
        var total = list.Count;
        // KULLANICI ISTEGI (2026-08-31): "muayene de gönderilen tanı ve işlem yoksada
        // yine yeşil olarak 0/0 gibi yazsın" -- hic kayit yoksa "yapilacak bir sey yok"
        // durumu, yesil (success) sayiliyor, gri (neutral) degil.
        if (total == 0) return ("success", "", 0, 0);
        var success = list.Count(BasariylaGonderildi);
        if (list.Any(d => d?.Status == SyncStatus.Failed)) return ("danger", "hatalı", success, total);
        if (success == total) return ("success", "başarılı", success, total);
        if (success == 0) return ("neutral", "gönderilmedi", success, total);
        return ("warning", "kısmi", success, total);
    }

    public (string CssClass, string Label, int Success, int Total) TanilarOzet() => Ozet(Tanilar.Select(t => t.Durum));
    public (string CssClass, string Label, int Success, int Total) IslemlerOzet() => Ozet(Islemler.Select(i => i.Durum));
    public (string CssClass, string Label, int Success, int Total) LabsOzet() => Ozet(LabGroups.Select(g => g.Durum));
    public (string CssClass, string Label, int Success, int Total) MuayineIcerikOzet() => Ozet(Tanilar.Select(t => t.Durum).Concat(Islemler.Select(i => i.Durum)));

    // KULLANICI ISTEGI (2026-08-29): "alt paremetreli testleri ayrı ayrı göstermesin ana
    // testin yanını artı ile gösterebilir" (ornek: EOS % hemogramin alt parametresi). Tek bir
    // panelde 20+ satir olabildigi icin (canli gorulen: 89 satirlik bir protokol) duz liste
    // okunmuyordu. PanelAdi (bkz. LabResultRecord) doluysa o satir bir alt parametredir --
    // panelin KENDI satiri (ornek: "Hemogram") panelAdi=null ama TetkikAdi'si baska
    // satirlarin PanelAdi'siyla ESLESIR, bu yuzden grup adi olarak kullanilabilir.
    // BAKANLIK ISTEGI (2026-09-16) SONRASI: bir panel artik TEK Observation olarak
    // gidiyor (alt parametreler component[] icinde), dolayisiyla durum da TEK -- eskiden
    // her alt parametrenin kendi SyncLog kaydi vardi ve rozet onlarin ozetiydi.
    // Gruplama LabGroupBuilder'da (cekirdek), gonderimle AYNI kod.
    public record LabGrupGorunum(LabGroupBuilder.LabGrup Grup, SyncLogEntry? Durum)
    {
        public string GroupName => Grup.Ad;
        public IReadOnlyList<LabResultRecord> Satirlar => Grup.TumSatirlar;
        public bool Panelli => Grup.Panelli;

        public (string CssClass, string Label) AggregateBadge() => Durum?.Status switch
        {
            SyncStatus.Failed => ("danger", "Hatalı"),
            SyncStatus.Skipped => ("warning", "Atlandı"),
            _ when BasariylaGonderildi(Durum) => ("success", "Gönderildi"),
            _ => ("neutral", "Gönderilmedi"),
        };

        public bool Gonderilebilir => !BasariylaGonderildi(Durum);
        public bool Silinebilir => SyncLogEntry.CanDelete(Durum);
    }
    public List<LabGrupGorunum> LabGroups { get; set; } = [];

    // Radyoloji (DiagnosticReport) -- Lab ile AYNI kalip. GetRadiologyReportsByProtokolIdAsync
    // sadece RIS.TetkikIslem.State=6 (onaylanmis/kesinlesmis) raporlari donduruyor, Icbari
    // eslesmesi ya da ilgili Procedure gonderimi eksikse RadiologyReportMapper Skipped doner.
    public List<(RadiologyReportRecord Report, SyncLogEntry? Durum)> RadiologyReports { get; set; } = [];
    public bool RadiologyGonderilebilir => RadiologyReports.Any(r => !BasariylaGonderildi(r.Durum));
    public bool RadiologySilinebilir => RadiologyReports.Any(r => SyncLogEntry.CanDelete(r.Durum));

    public (string CssClass, string Label) RadiologyAggregateBadge()
    {
        if (RadiologyReports.Count == 0) return ("neutral", "Gönderilmedi");
        if (RadiologyReports.Any(x => x.Durum?.Status == SyncStatus.Failed)) return ("danger", "Hatalı");
        if (RadiologyReports.All(x => BasariylaGonderildi(x.Durum))) return ("success", "Gönderildi");
        if (RadiologyReports.Any(x => BasariylaGonderildi(x.Durum))) return ("warning", "Kısmen gönderildi");
        return ("neutral", "Gönderilmedi");
    }

    public (string CssClass, string Label, int Success, int Total) RadiologyOzet() => Ozet(RadiologyReports.Select(r => r.Durum));

    // Patoloji (DiagnosticReport) -- Radyoloji ile AYNI kalip. GetPathologyReportsByProtokolIdAsync
    // sadece EMR.Pathology.Result.ReportState=4 ("Onaylanmis") raporlari donduruyor, Icbari
    // eslesmesi ya da ilgili Islem gonderimi eksikse PathologyReportMapper Skipped doner.
    public List<(PathologyReportRecord Report, SyncLogEntry? Durum)> PathologyReports { get; set; } = [];
    public bool PathologyGonderilebilir => PathologyReports.Any(r => !BasariylaGonderildi(r.Durum));
    public bool PathologySilinebilir => PathologyReports.Any(r => SyncLogEntry.CanDelete(r.Durum));

    public (string CssClass, string Label) PathologyAggregateBadge()
    {
        if (PathologyReports.Count == 0) return ("neutral", "Gönderilmedi");
        if (PathologyReports.Any(x => x.Durum?.Status == SyncStatus.Failed)) return ("danger", "Hatalı");
        if (PathologyReports.All(x => BasariylaGonderildi(x.Durum))) return ("success", "Gönderildi");
        if (PathologyReports.Any(x => BasariylaGonderildi(x.Durum))) return ("warning", "Kısmen gönderildi");
        return ("neutral", "Gönderilmedi");
    }

    public (string CssClass, string Label, int Success, int Total) PathologyOzet() => Ozet(PathologyReports.Select(r => r.Durum));

    // Baslik satirindaki "Tümünü Sil"/"Tümünü Gönder" butonlarinin gorunurlugu -- KULLANICI
    // ISTEGI (2026-08-25): "tanı ve procedür başlıklarında tümünü sil ve eğer silinmiş ise
    // tümünü gönder butonu olmalı". Silinebilir olan (en az bir AzResourceId'li, henuz
    // silinmemis kayit) varsa Sil butonu; basariyla gonderilmemis (hic denenmemis, hatali
    // veya silinmis) en az bir kayit varsa Gönder butonu gosterilir.
    public bool TanilarSilinebilir => Tanilar.Any(t => SyncLogEntry.CanDelete(t.Durum));
    public bool TanilarGonderilebilir => Tanilar.Any(t => !BasariylaGonderildi(t.Durum));
    public bool IslemlerSilinebilir => Islemler.Any(i => SyncLogEntry.CanDelete(i.Durum));
    public bool IslemlerGonderilebilir => Islemler.Any(i => !BasariylaGonderildi(i.Durum));

    // KULLANICI ISTEGI (2026-08-25, dorduncu tur): "tanıyı sildim silinemedi yazısı
    // yazıyordu sonradan tekrar gönder dedim hata da kaldı ... böyle hiç bir zaman
    // kalmamalıyım bunu bir şekilde ya silmeli yada tekrar gönder yapabilmeliyim" -- eger
    // basarisiz bir deneme sonucunda AzResourceId null'a duserse (orn. Create de basarisiz
    // olursa) CanDelete de false donuyor, sadece TOPLU "Tümünü Gönder" kaliyor -- tekil
    // satirda ne Sil ne Detay ne de tekil Gönder vardi, kullanici hatanin SEBEBINI bile
    // goremiyordu. Artik public -- cshtml'de her satirda kullanılıyor.
    public static bool BasariylaGonderildi(SyncLogEntry? durum) =>
        durum is { Status: SyncStatus.Success } && durum.Operation != SyncOperation.Delete;
    public bool MuayineGonderimeUygun { get; set; }
    public int OpenProtokolSendAfterDays { get; set; }
    public bool EpikrizSendEnabled { get; set; }
    public bool EpikrizOnlySigned { get; set; }

    // Bir bolumun Pusula sorgusu zaman asimina ugrarsa SAYFANIN TAMAMI 500 ile olurdu
    // (sunucuda 2026-09-14'te yasandi: GetLabResultsByProtokolIdAsync -> "Execution
    // Timeout Expired", Error -2). Protokol Detay'in tek islevi gonderim yapmak;
    // laboratuvar okunamadi diye Hasta/Muayine/Tani/Islem/Radyoloji/Patoloji gonderiminin
    // de kilitlenmesi kabul edilemez. Artik SADECE o bolum bos kaliyor ve sayfanin
    // basinda neyin okunamadigi aciqca yaziyor.
    //
    // YALNIZCA zaman asimi (-2) ve deadlock (1205) yakalaniyor -- gercek bir hata
    // (yanlis kolon adi, yetki, bozuk SQL) eskisi gibi patlasin, sessizce bos bir bolum
    // olarak gizlenmesin.
    public List<string> BolumHatalari { get; } = [];

    private async Task<T> BolumOkuAsync<T>(string bolum, Func<Task<T>> oku, T bosDeger)
    {
        try
        {
            return await oku();
        }
        catch (SqlException ex) when (ex.Number is -2 or 1205)
        {
            var neden = ex.Number == -2 ? "sorgu zaman aşımına uğradı" : "veritabanı kilitlenmesi (deadlock)";
            BolumHatalari.Add($"{bolum} okunamadı -- {neden}. Bu bölüm boş görünüyor, Pusula'da veri olabilir; gönderim yapmadan önce sayfayı yenileyin.");
            logger.LogWarning(ex, "Protokol Detay: {Bolum} okunamadi (SQL {Number})", bolum, ex.Number);
            return bosDeger;
        }
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        Hasta = await pusulaRepository.GetHastaByIdAsync(Protokol.HastaId, ct);
        var hastaStatuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [Protokol.HastaId], ct);
        HastaDurumKaydi = hastaStatuses.GetValueOrDefault(Protokol.HastaId);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [Protokol.ProtokolId], ct);
        MuayineDurumKaydi = encounterStatuses.GetValueOrDefault(Protokol.ProtokolId);
        var compositionStatuses = await syncLog.GetLatestByPusulaIdsAsync("Composition", [Protokol.ProtokolId], ct);
        EpikrizDurumKaydi = compositionStatuses.GetValueOrDefault(Protokol.ProtokolId);

        // Tanı (Condition) ve İşlem (Procedure) -- KULLANICI ISTEGI (2026-08-25): "bu
        // yaptıklarımızı sistemimize ilave etmemişsin" -- ikisi de Müayinə cascade'iyle
        // otomatik gönderiliyordu ama Protokol Detay'da hiç görünmüyorlardı, sadece
        // Aktivite Akışı'ndan bulunabiliyorlardı. Ayrı bir "Gönder" butonu yok (cascade
        // zaten Müayinə "Gönder"ine bağlı), sadece son durum gösteriliyor.
        var tanilar = await BolumOkuAsync("Tanı", () => pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct), []);
        var taniStatuses = await syncLog.GetLatestByPusulaIdsAsync("Condition", tanilar.Select(t => t.Id).ToList(), ct);
        Tanilar = tanilar.Select(t => (t, taniStatuses.GetValueOrDefault(t.Id))).ToList();

        var islemler = await BolumOkuAsync("İşlem", () => pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct), []);
        var islemStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", islemler.Select(i => i.Id).ToList(), ct);
        Islemler = islemler.Select(i => (i, islemStatuses.GetValueOrDefault(i.Id))).ToList();

        var labs = await BolumOkuAsync("Laboratuvar", () => pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct), []);
        var labGruplari = LabGroupBuilder.Build(labs);
        var labStatuses = await syncLog.GetLatestByPusulaIdsAsync(
            "Observation", labGruplari.Select(g => g.AnahtarId).ToList(), ct);
        LabGroups = labGruplari.Select(g => new LabGrupGorunum(g, labStatuses.GetValueOrDefault(g.AnahtarId))).ToList();
        Labs = labs.Select(l => (l, (SyncLogEntry?)null)).ToList();

        var radiologyReports = await BolumOkuAsync("Radyoloji", () => pusulaRepository.GetRadiologyReportsByProtokolIdAsync(Protokol.ProtokolId, ct), []);
        var radiologyStatuses = await syncLog.GetLatestByPusulaIdsAsync("DiagnosticReport", radiologyReports.Select(r => r.TetkikIslemId).ToList(), ct);
        RadiologyReports = radiologyReports.Select(r => (r, radiologyStatuses.GetValueOrDefault(r.TetkikIslemId))).ToList();

        var pathologyReports = await BolumOkuAsync("Patoloji", () => pusulaRepository.GetPathologyReportsByProtokolIdAsync(Protokol.ProtokolId, ct), []);
        var pathologyStatuses = await syncLog.GetLatestByPusulaIdsAsync("DiagnosticReport-Patoloji", pathologyReports.Select(r => r.ResultId).ToList(), ct);
        PathologyReports = pathologyReports.Select(r => (r, pathologyStatuses.GetValueOrDefault(r.ResultId))).ToList();

        GenelMuayene = await BolumOkuAsync("Epikriz/Genel Muayene", () => pusulaRepository.GetGenelMuayeneByProtokolIdAsync(Protokol.ProtokolId, ct), null);
        EpikrizSendEnabled = await settings.GetBoolAsync(SettingsStore.EpikrizSendEnabledKey, true, ct);
        EpikrizOnlySigned = await settings.GetBoolAsync(SettingsStore.EpikrizOnlySignedKey, true, ct);

        OpenProtokolSendAfterDays = await settings.GetIntAsync(
            SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);
        MuayineGonderimeUygun = EncounterMapper.IsEligibleForSend(Protokol.AcilisTarihi, Protokol.KapanisTarihi, OpenProtokolSendAfterDays);
        return Page();
    }

    // KARAR (2026-08-20, kullanici istegi): panelden "Gonder" artik CANLI gonderim yapar
    // (liveMode:true) -- eskiden sadece $validate calisiyordu. Muayine gonderiminde hasta
    // e-Health'te yoksa EncounterSyncService onu otomatik olarak once canli gonderir,
    // kullanicinin ayrica "once hastayi gonder" diye ugrasmasina gerek kalmaz.
    // Master "Tümünü Gönder" -- KULLANICI ISTEGI (2026-08-27): "e-Health gönderim durumu
    // alanının yanına tümünü gönder butonu koyalım" -- baslik satirinda tek tikla butun
    // protokolu gonderen bir kisayol. EncounterSyncService.SyncOneAsync zaten Hasta ->
    // Muayine -> Tani -> Islem'i CASCADE olarak gonderiyor (bkz. o dosyadaki SyncOneAsync),
    // burada ayrica tek tek cagirmaya gerek yok -- sadece cascade'e DAHIL OLMAYAN Epikriz'i
    // (Composition) ayrica gonderiyoruz. Reçete protokolleri e-Health'e hic gonderilmedigi
    // icin (sayfadaki diger butonlar gibi) bu durumda hicbir sey yapmiyor.
    // DUZELTME (2026-08-31, kullanici: "hasta bilgilerine yaptığımız tümünü gönder butonu lab
    // ve rad için gönderim yapmıyor"): Lab hicbir zaman Encounter cascade'inin parcasi degildi
    // (Patient'e bagli tasarim, Muayine'den bagimsiz gonderilebilir -- bkz. LabResultSyncService
    // basindaki not), Radyoloji ise cascade'e dahil (EncounterSyncService.SyncRadiologyReportsAsync)
    // ama SADECE o an gonderilen Islem'lerin AZ id'sine bagli oldugu icin sonucu buradan
    // GOZLEMLENEMIYORDU. "Tümünü Gönder" adindan beklenti acikca "gercekten her sey" oldugu icin
    // Lab ve Radyoloji de ayni "sadece basarisiz/gonderilmemis olanlari gonder" kuraliyla BURADA
    // da aciqca tetikleniyor -- OnPostTumunuGonderLabAsync/OnPostTumunuGonderRadiologyAsync ile
    // AYNI paylasilan metotlar (SendAllLabsAsync/SendAllRadiologyAsync).
    public async Task<IActionResult> OnPostTumunuGonderAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        // Zincirin tamami ProtocolFullSyncService'te -- Protokol Listesi'ndeki toplu
        // gonderim ve AutoSyncWorker de AYNI metodu cagiriyor, boylece uc yol arasinda
        // fark olusamiyor (2026-09-15'te tam bu fark yuzunden epikriz/lab/patoloji
        // toplu gonderimde hic gitmiyordu). Recete kontrolu de servisin icinde.
        await protocolFullSync.SyncAllAsync(Protokol, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tumunu Sil -- ust bardaki "Tümünü Gönder"in karsiligi (KULLANICI ISTEGI 2026-09-14).
    // Bolum bolum silmek yerine protokolun tum gonderimlerini tek hamlede geri alir.
    //
    // Silme SIRASI ve "Patient silinmez" karari burada TEKRARLANMIYOR: iptal senkronunun
    // kullandigi CancellationSyncService.DeleteProtocolChainAsync cagriliyor. Sira distan
    // ice olmak zorunda (raporlar -> epikriz/islem/tani -> Muayine) cunku FHIR hala
    // referans verilen bir kaynagi silmeyi HTTP 409 ile reddeder.
    //
    // HASTA (Patient) BILEREK SILINMEZ: ayni hasta baska protokollerde de kullaniliyor,
    // bir protokolun sayfasindan silmek digerlerini kirardi. Hasta satirinin kendi "Sil"
    // dugmesi bunun icin duruyor.
    public async Task<IActionResult> OnPostTumunuSilAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await cancellationSyncService.DeleteProtocolChainAsync(
            Protokol.ProtokolId, $"Protokol Detay \"Tümünü Sil\" ({Protokol.ProtokolId})", ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderHastaAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await patientSyncService.SyncOneAsync(Protokol.HastaId, liveMode: true, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderMuayineAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await encounterSyncService.SyncOneAsync(Protokol.ProtokolId, liveMode: true, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Yanlislikla gonderilmis Hasta/Muayine kaydini e-Health'ten geri almak icin.
    public async Task<IActionResult> OnPostSilHastaAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [Protokol.HastaId], ct);
        var latest = statuses.GetValueOrDefault(Protokol.HastaId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilMuayineAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [Protokol.ProtokolId], ct);
        var latest = statuses.GetValueOrDefault(Protokol.ProtokolId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderEpikrizAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await compositionSyncService.SyncOneAsync(Protokol.ProtokolId, liveMode: true, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilEpikrizAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Composition", [Protokol.ProtokolId], ct);
        var latest = statuses.GetValueOrDefault(Protokol.ProtokolId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tanı/İşlem satır bazlı silme -- KULLANICI ISTEGI (2026-08-25): "ama bence göndermeyi
    // otomatik yapabiliriz. fakat silme olmalı kesin." Otomatik gönderilenler (cascade)
    // yanlışlıkla/gereksiz gitmiş olabilir, bu yüzden her satırda tek tek silinebilmeli --
    // genel Detail sayfasındaki "Sil" zaten çalışıyordu ama buradan (Protokol Detay'dan)
    // tıklamak icin ayrı bir tur almaya gerek kalmasin diye dogrudan buraya da eklendi.
    public async Task<IActionResult> OnPostSilTaniAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilIslemAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tanı/İşlem satır bazlı (tekil) tekrar gönderim -- KULLANICI ISTEGI (2026-08-25):
    // "böyle hiç bir zaman kalmamalıyım bunu bir şekilde ya silmeli yada tekrar gönder
    // yapabilmeliyim" -- CanDelete false donduren (AzResourceId'i olmayan, basarisiz)
    // satirlar icin de tekil bir "Gönder" secenegi olsun diye -- eskiden sadece TOPLU
    // "Tümünü Gönder" vardi, tek bir kaydi hedefli sekilde tekrar denemek mumkun degildi.
    public async Task<IActionResult> OnPostGonderTaniAsync(int id, int taniId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
            var tani = tanilar.FirstOrDefault(t => t.Id == taniId);
            if (tani is not null)
                await conditionSyncService.SyncOneAsync(tani, Protokol, azPatientId, azEncounterId, tanilar.Count, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderIslemAsync(int id, int islemId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
            var islem = islemler.FirstOrDefault(i => i.Id == islemId);
            if (islem is not null)
                await procedureSyncService.SyncOneAsync(islem, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    // Tümünü Sil -- SADECE şu an başarıyla gönderilmiş (silinebilir) tanıları siler, tek
    // tek "Sil" ile birebir aynı DeleteService cagrisini her satir icin ayri ayri yapar --
    // KULLANICI ISTEGI (2026-08-25): "eğer listeden bir işlem yada tanı silinirse sadece o
    // silinmeli" -- yani bu bulk islem de aslinda birbirinden bagimsiz N tekil silme, ortak
    // bir "hepsini birden iptal et" cagrisi YOK, bu yuzden biri basarisiz olsa bile digerleri
    // etkilenmez.
    public async Task<IActionResult> OnPostTumunuSilTaniAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
        var taniStatuses = await syncLog.GetLatestByPusulaIdsAsync("Condition", tanilar.Select(t => t.Id).ToList(), ct);
        foreach (var durum in taniStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuSilIslemAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
        var islemStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", islemler.Select(i => i.Id).ToList(), ct);
        foreach (var durum in islemStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tümünü Gönder -- SADECE henüz başarıyla gönderilmemiş (hiç denenmemiş/hatalı/silinmiş)
    // tanıları/işlemleri gönderir, halihazırda başarılı olanlara DOKUNMAZ -- ayni "sadece o
    // silinmeli, digerleri kalsin" mantiginin gonderim tarafindaki karsiligi. Encounter'in
    // gercek AZ id'si zaten bilinmiyorsa (Muayine hic gonderilmemisse) yapacak bir sey yok --
    // once Muayine "Gönder" ile gonderilmeli.
    public async Task<IActionResult> OnPostTumunuGonderTaniAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
            var taniStatuses = await syncLog.GetLatestByPusulaIdsAsync("Condition", tanilar.Select(t => t.Id).ToList(), ct);
            foreach (var tani in tanilar)
            {
                if (BasariylaGonderildi(taniStatuses.GetValueOrDefault(tani.Id))) continue;
                await conditionSyncService.SyncOneAsync(tani, Protokol, azPatientId, azEncounterId, tanilar.Count, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuGonderIslemAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
            var islemStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", islemler.Select(i => i.Id).ToList(), ct);
            foreach (var islem in islemler)
            {
                if (BasariylaGonderildi(islemStatuses.GetValueOrDefault(islem.Id))) continue;
                await procedureSyncService.SyncOneAsync(islem, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    // DUZELTME (2026-08-25, canli olayda bulundu): Kullanici bir Tanı'yı sildi (409 "hala
    // referans ediliyor" ile reddedildi -- Encounter.diagnosis hala isaret ediyordu), sonra
    // "Tekrar Gönder" dedi ve "HTTP 409: Non-existent reference: Encounter/..." hatasi aldi.
    // Kok neden: bizim SyncLogStore'daki "en son basarili Encounter" kaydi ESKI/GECERSIZ --
    // o Encounter e-Health sunucusunda ARTIK YOK (bizim tarafimizdan silinmedi, SyncLog'da
    // boyle bir Delete kaydi yok -- disaridan/sunucu tarafinda kaybolmus), ama biz hala o
    // ID'yi "gecerli" sanip Condition/Procedure'a referans olarak gonderiyorduk, sonsuza
    // kadar ayni 409'u alacak sekilde. Artik kullanmadan once GERCEKTEN var mi diye canli
    // kontrol ediliyor -- yoksa Muayine kendi kendini iyilestiriyor (EncounterSyncService
    // zaten FindExistingIdAsync ile canli arama yapip bulamazsa YENİ bir Encounter olusturur
    // ve cascade ile Tanı/İşlem'i de otomatik yeniden gonderir).
    public async Task<IActionResult> OnPostSilLabAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuSilLabAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
        var gruplar = LabGroupBuilder.Build(labs);
        var labStatuses = await syncLog.GetLatestByPusulaIdsAsync("Observation", gruplar.Select(g => g.AnahtarId).ToList(), ct);
        foreach (var durum in labStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderLabAsync(int id, int labId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(Protokol, ct);
        if (azPatientId is not null)
        {
            // Tek bir alt parametre ARTIK tek basina gonderilemiyor -- panelin component'i
            // oldugu icin kendi kaynagi yok. Bu yuzden satirin ait oldugu GRUP gonderiliyor.
            var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
            var grup = LabGroupBuilder.Build(labs)
                .FirstOrDefault(g => g.TumSatirlar.Any(l => l.LabaratuarSonucId == labId));
            if (grup is not null)
                await labResultSyncService.SyncGroupAsync(grup, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuGonderLabAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await SendAllLabsAsync(Protokol, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    private async Task SendAllLabsAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(protokol, ct);
        if (azPatientId is null) return;

        var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(protokol.ProtokolId, ct);
        var gruplar = LabGroupBuilder.Build(labs);
        var labStatuses = await syncLog.GetLatestByPusulaIdsAsync("Observation", gruplar.Select(g => g.AnahtarId).ToList(), ct);
        foreach (var grup in gruplar)
        {
            if (BasariylaGonderildi(labStatuses.GetValueOrDefault(grup.AnahtarId))) continue;
            await labResultSyncService.SyncGroupAsync(grup, protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
    }

    // KULLANICI ISTEGI (2026-08-29): "grup olan testlerin satırına ... gönder butonu
    // tıklandığında tüm alt parametreleri hepsini göndersin" -- OnPostTumunuGonderLabAsync
    // ile AYNI kalip ama SADECE tek bir panele (grupAdi) ait olanlar. BuildLabGroups AYNEN
    // ekranda gosterilen gruplamayla ayni sonucu versin diye burada da cagriliyor.
    public async Task<IActionResult> OnPostGonderLabGrubuAsync(int id, string grupAdi, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(Protokol, ct);
        if (azPatientId is not null)
        {
            var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
            var grup = LabGroupBuilder.Build(labs).FirstOrDefault(g => g.Ad == grupAdi);
            if (grup is not null)
                await labResultSyncService.SyncGroupAsync(grup, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilLabGrubuAsync(int id, string grupAdi, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
        var grup = LabGroupBuilder.Build(labs).FirstOrDefault(g => g.Ad == grupAdi);
        if (grup is not null)
        {
            var durumlar = await syncLog.GetLatestByPusulaIdsAsync("Observation", [grup.AnahtarId], ct);
            var durum = durumlar.GetValueOrDefault(grup.AnahtarId);
            if (SyncLogEntry.CanDelete(durum))
                await deleteService.DeleteAsync(durum!, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    // Tanı/İşlem'deki GetGercekIdleriAsync'in aksine burada Muayine (Encounter) HENUZ
    // gonderilmemisse otomatik gondermeye ZORLAMIYORUZ -- Observation.encounter opsiyonel
    // (bkz. LabResultObservationMapper), lab sonucu tek basina (sadece Hasta'ya bagli olarak)
    // gonderilebilir. Kayitli Encounter id'si varsa GERCEKTEN gecerli mi diye canli kontrol
    // ediliyor (GetGercekIdleriAsync'teki ayni "409 riskini onle" mantigi) -- gecersizse
    // sadece referans eklenmiyor, yeni bir Encounter OLUSTURULMUYOR.
    //
    // DUZELTME (2026-08-31, protokol 50819013 -- kullanici: "tüm bilgileri yeniden
    // göndermeme rağmen ... Bağlı olduğu Hasta kaydı e-Health'te artık bulunamıyor"):
    // azPatientId ESKIDEN SyncLog'daki en son BASARILI Patient gonderiminin AzResourceId'sini
    // KORU SORGULAMADAN kullaniyordu -- Muayine/Epikriz ayni anda basariyla gonderilebiliyordu
    // (canli kanit: bu protokolde ikisi de basarili) cunku EncounterSyncService (satir 48)
    // Patient'i HIC bizim SyncLog'umuzdan degil, DOGRUDAN CANLI eHealthClient.FindExistingIdAsync
    // ile arıyor. SyncLog'daki kayitli id ile e-Health'teki GERCEK id farklilasabiliyor (orn.
    // hasta e-Health tarafinda yeniden olusturulmus/id degismis) -- bu durumda Lab/Tani/Islem
    // hala ESKI/gecersiz id'yi gonderip "Non-existent reference: Patient/..." aliyordu. Artik
    // Encounter ile AYNI sekilde CANLI arama yapiyoruz -- cache'e guvenmiyoruz.
    private async Task<(string? AzPatientId, string? AzEncounterId)> GetIdleriLabIcinAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId], ct);
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
            if (!check.Success)
                azEncounterId = null;
        }

        return (azPatientId, azEncounterId);
    }

    // DUZELTME (2026-08-31): GetIdleriLabIcinAsync'teki AYNI gerekce -- azPatientId artik
    // SyncLog cache'i yerine CANLI eHealthClient.FindExistingIdAsync ile araniyor (Encounter'in
    // kendi Patient cozumleme mantigiyla BIREBIR ayni), stale/gecersiz id yuzunden Tanı/İşlem
    // gonderiminin "Non-existent reference: Patient/..." almasini onlemek icin.
    private async Task<(string? AzPatientId, string? AzEncounterId)> GetGercekIdleriAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId], ct);
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
            if (!check.Success)
                azEncounterId = null; // kayitli id artik gecersiz -- asagida yeniden gonderilecek
        }

        if (azEncounterId is null)
        {
            var encResult = await encounterSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);
            azEncounterId = encResult.AzResourceId;
        }

        return (azPatientId, azEncounterId);
    }

    // Radyoloji manuel "Gönder" -- Lab'daki GetIdleriLabIcinAsync ile AYNI (Encounter canli
    // dogrulanir, yoksa YENIDEN OLUSTURULMAZ, sadece referans eklenmez). related-procedure ve
    // performer icin BURADA cascade/otomatik gonderim YAPILMIYOR -- ikisi de normalde Muayine
    // gonderildiginde EncounterSyncService.SyncRadiologyReportsAsync tarafindan zaten
    // cozulmus/gonderilmis olur; bu sadece o SONUCU (SyncLog'daki mevcut durumu) okur.
    // Procedure hic gonderilmemisse (orn. Icbari eslesmesi yok) manuel Gönder de RadiologyReportMapper
    // tarafindan ayni sekilde Skipped'e duser -- kullanici once İşlem'i cozmeli.
    private async Task<(string? AzProcedureId, string? AzPractitionerId)> GetRadiologyBaglantiIdleriAsync(RadiologyReportRecord report, CancellationToken ct)
    {
        var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", [report.ProtokolIslemId], ct);
        var azProcedureId = procedureStatuses.GetValueOrDefault(report.ProtokolIslemId) is { Status: SyncStatus.Success, AzResourceId: not null } proc
            ? proc.AzResourceId
            : null;

        string? azPractitionerId = null;
        if (report.RaporuOnaylayanDoktorId is { } doktorId)
        {
            var practitionerStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId], ct);
            azPractitionerId = practitionerStatuses.GetValueOrDefault(doktorId) is { Status: SyncStatus.Success, AzResourceId: not null } prac
                ? prac.AzResourceId
                : null;
        }

        return (azProcedureId, azPractitionerId);
    }

    public async Task<IActionResult> OnPostGonderRadiologyAsync(int id, int tetkikIslemId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(Protokol, ct);
        if (azPatientId is not null)
        {
            var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(Protokol.ProtokolId, ct);
            var report = reports.FirstOrDefault(r => r.TetkikIslemId == tetkikIslemId);
            if (report is not null)
            {
                var (azProcedureId, azPractitionerId) = await GetRadiologyBaglantiIdleriAsync(report, ct);
                await radiologyReportSyncService.SyncOneAsync(report, Protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuGonderRadiologyAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await SendAllRadiologyAsync(Protokol, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    private async Task SendAllRadiologyAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(protokol, ct);
        if (azPatientId is null) return;

        var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
        var radiologyStatuses = await syncLog.GetLatestByPusulaIdsAsync("DiagnosticReport", reports.Select(r => r.TetkikIslemId).ToList(), ct);
        foreach (var report in reports)
        {
            if (BasariylaGonderildi(radiologyStatuses.GetValueOrDefault(report.TetkikIslemId))) continue;
            var (azProcedureId, azPractitionerId) = await GetRadiologyBaglantiIdleriAsync(report, ct);
            await radiologyReportSyncService.SyncOneAsync(report, protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
        }
    }

    public async Task<IActionResult> OnPostSilRadiologyAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuSilRadiologyAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(Protokol.ProtokolId, ct);
        var radiologyStatuses = await syncLog.GetLatestByPusulaIdsAsync("DiagnosticReport", reports.Select(r => r.TetkikIslemId).ToList(), ct);
        foreach (var durum in radiologyStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Patoloji manuel "Gönder" -- Radyoloji ile AYNI kalip (bkz. o metottaki gerekce).
    private async Task<(string? AzProcedureId, string? AzPractitionerId)> GetPathologyBaglantiIdleriAsync(PathologyReportRecord report, CancellationToken ct)
    {
        string? azProcedureId = null;
        if (report.ProtokolIslemId is { } patolojiIslemId)
        {
            var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", [patolojiIslemId], ct);
            azProcedureId = procedureStatuses.GetValueOrDefault(patolojiIslemId) is { Status: SyncStatus.Success, AzResourceId: not null } proc
                ? proc.AzResourceId
                : null;
        }

        string? azPractitionerId = null;
        if (report.ApprovedById is { } doktorId)
        {
            var practitionerStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId], ct);
            azPractitionerId = practitionerStatuses.GetValueOrDefault(doktorId) is { Status: SyncStatus.Success, AzResourceId: not null } prac
                ? prac.AzResourceId
                : null;
        }

        return (azProcedureId, azPractitionerId);
    }

    public async Task<IActionResult> OnPostGonderPathologyAsync(int id, int resultId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(Protokol, ct);
        if (azPatientId is not null)
        {
            var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(Protokol.ProtokolId, ct);
            var report = reports.FirstOrDefault(r => r.ResultId == resultId);
            if (report is not null)
            {
                var (azProcedureId, azPractitionerId) = await GetPathologyBaglantiIdleriAsync(report, ct);
                await pathologyReportSyncService.SyncOneAsync(report, Protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuGonderPathologyAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await SendAllPathologyAsync(Protokol, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    private async Task SendAllPathologyAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(protokol, ct);
        if (azPatientId is null) return;

        var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
        var pathologyStatuses = await syncLog.GetLatestByPusulaIdsAsync("DiagnosticReport-Patoloji", reports.Select(r => r.ResultId).ToList(), ct);
        foreach (var report in reports)
        {
            if (BasariylaGonderildi(pathologyStatuses.GetValueOrDefault(report.ResultId))) continue;
            var (azProcedureId, azPractitionerId) = await GetPathologyBaglantiIdleriAsync(report, ct);
            await pathologyReportSyncService.SyncOneAsync(report, protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
        }
    }

    public async Task<IActionResult> OnPostSilPathologyAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuSilPathologyAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(Protokol.ProtokolId, ct);
        var pathologyStatuses = await syncLog.GetLatestByPusulaIdsAsync("DiagnosticReport-Patoloji", reports.Select(r => r.ResultId).ToList(), ct);
        foreach (var durum in pathologyStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

}
