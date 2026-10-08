using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Bir protokolun BUTUN TRƏS gonderimini tek yerde toplar: Hasta -> Muayine ->
// Tani -> Islem (EncounterSyncService cascade'i) + Epikriz + Laboratuvar + Radyoloji
// + Patoloji.
//
// NEDEN VAR (2026-09-15, kullanici bildirdi: "secilenleri gonder butonuna tiklayinca
// protokoldeki tum gonderimleri yapmamaktadir, ornek olarak epikriz"):
//
// Bu zincir eskiden SADECE Protokol Detay sayfasinda, o sayfanin private metotlarinda
// yaziliydi. Ayni isi yapmasi gereken diger iki yer yalnizca
// EncounterSyncService.SyncOneAsync cagiriyordu ve Epikriz/Lab/Patolojiyi HIC
// gondermiyordu:
//   1. Protokol Listesi'ndeki "Seçilenleri Gönder" (toplu gonderim)
//   2. AutoSyncWorker (otomatik gonderim dongusu -- henuz hic acilmadi)
//
// SOMUT ZARAR OLCULDU: sunucuda toplu gonderimle giden 50833609, 50845123 ve 50847194
// protokollerinde 222 onayli laboratuvar sonucunun HICBIRI gonderilmemis, senkron
// gunlugunde tek bir deneme kaydi bile yok. Bakanlik bu protokolleri laboratuvarsiz
// gordu.
//
// Ayni hata iki kez tekrarlandi (once Lab+Radyoloji 484f530'da, sonra Patoloji
// 7f50755'te yalnizca Protokol Detay'a eklendi), bu yuzden cozum "diger cagri
// yerlerine de ekleyelim" degil: zincir ARTIK TEK BIR YERDE. Yeni bir kaynak tipi
// eklendiginde burasi guncellenir ve uc cagri yeri de kendiliginden dogru olur.
//
// AYAR KAPISI (2026-09-28 inceleme bulgusu): radyoloji ve patoloji gonderiminin
// acik/kapali anahtarlari (RadiologyReport.SendEnabled, PathologyReport.SendEnabled)
// YALNIZCA EncounterSyncService'in cascade'inde denetleniyordu. Zincir buraya
// tasinirken kontroller birlikte tasinmamis -- yani bu servis anahtarlar kapaliyken
// de gonderiyordu. Iki anahtar bugun Ayarlar ekraninda gosterilmedigi (hep varsayilan
// true oldugu) icin pratikte bir zarar dogmamis; ama otomatik gonderim bu servis
// uzerinden calisacagi icin kapi burada da kuruldu.
public class ProtocolFullSyncService(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    SettingsStore settings,
    EHealthClient eHealthClient,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    CompositionSyncService compositionSyncService,
    LabResultSyncService labResultSyncService,
    RadiologyReportSyncService radiologyReportSyncService,
    PathologyReportSyncService pathologyReportSyncService,
    VitalSignsSyncService vitalSignsSyncService)
{
    public record Sonuc(SyncStatus EncounterStatus, int Epikriz, int Lab, int Radyoloji, int Patoloji, int Vital)
    {
        public static Sonuc Bos(SyncStatus s) => new(s, 0, 0, 0, 0, 0);
    }

    public async Task<Sonuc> SyncAllAsync(int protokolId, CancellationToken ct = default)
    {
        var protokol = await pusulaRepository.GetProtokolByIdAsync(protokolId, ct);
        if (protokol is null) return Sonuc.Bos(SyncStatus.Skipped);
        return await SyncAllAsync(protokol, ct);
    }

    public async Task<Sonuc> SyncAllAsync(ProtokolListItem protokol, CancellationToken ct = default)
    {
        // Recete protokolleri TRƏS'e hic gonderilmiyor (Protokol Detay'daki
        // butonlarin tamami da bu durumda gizli).
        if (protokol.ProtokolTipiId == EncounterMapper.ReceteProtokolTipiId)
            return Sonuc.Bos(SyncStatus.Skipped);

        // 0) HASTA -- ACIKCA GONDERILIYOR (2026-10-02, kullanici bildirdi: "bu protokolde
        //    hasta bilgi alanında tümünü gönder dedim, hasta gönderilmedi, hiç denemedi bile").
        //
        //    KOK NEDEN: bu zincir hastayi EncounterSyncService'in cascade'ine birakiyordu,
        //    o da yalnizca hasta TRƏS'te YOKSA gonderiyor (FindExistingIdAsync null donerse).
        //    Hasta TRƏS'te zaten varsa -- ki cogu zaman vardir -- bulunan AZ id dogrudan
        //    kullaniliyor ve PatientSyncService HIC cagrilmiyordu. Sonucu iki kat kotu:
        //      - SyncLog'a tek bir Patient kaydi bile yazilmiyor, dolayisiyla Protokol
        //        Detay'daki Hasta satiri sonsuza kadar "Gönderilmedi" diyor;
        //      - butonun kendi onay metni "Hasta, Müayinə, Tanı, ... gönderilecek" diyor,
        //        yani ekran soyledigini yapmiyordu.
        //
        //    PatientSyncService zaten idempotent (varsa Update, yoksa Create), bu yuzden
        //    acikca cagirmak guvenli. BEDELI: ayni hastanin birden cok protokolu tek turda
        //    gonderilirse hasta birkac kez PUT ediliyor. Bakanligin "her seferinde
        //    gondermeyin" uyarisi DOKTOR icindi (sabit, kucuk bir kume her Encounter'da
        //    tekrar tekrar gidiyordu); hasta protokol basina bir kez gidiyor ve demografi
        //    bilgisini guncel tutuyor -- kabul edilebilir bir takas.
        //
        //    YALNIZCA ZATEN VARSA (2026-10-05): eskiden kosulsuz cagriliyordu ve hasta
        //    TRƏS'te YOKSA ayni gonderim bir de asagidaki Encounter cascade'i tarafindan
        //    yapiliyordu -- cunku o da "hasta yoksa once hastayi gonder" diyor. Hasta
        //    gonderilemeyen bir kayitsa (orn. FIN bicimi hatali) ayni hata ayni saniyede
        //    IKI kez uretiliyordu. Canli gunlukte ucer ucer tekrarlanan "Hastanin FIN
        //    numarasi TRƏS'in bekledigi bicimde degil" satirlarinin kaynagi buydu.
        //
        //    Hasta YOKSA cascade zaten olusturuyor, yani "hic denenmedi" sorunu geri
        //    gelmiyor: her iki durumda da tam olarak BIR deneme ve BIR SyncLog kaydi olur.
        var azHastaVarMi = await eHealthClient.FindExistingIdAsync(
            "Patient", protokol.HastaId.ToString(), ct);
        if (azHastaVarMi is not null)
            await patientSyncService.SyncOneAsync(protokol.HastaId, liveMode: true, ct);

        // 1) Muayine -> Tani -> Islem (cascade EncounterSyncService'te)
        var encounter = await encounterSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);

        // 2) Epikriz -- cascade'e DAHIL DEGIL, ayrica cagrilmali.
        //
        //    MUAYINE BASARISIZSA CAGRILMIYOR (2026-10-05). Composition.encounter (1..1)
        //    zorunlu; muayine gonderilemediyse epikriz de gonderilemez. Ama
        //    CompositionSyncService bunu kendisi fark edip EncounterSyncService'i BASTAN
        //    calistiriyor -- o da hasta yoksa hastayi yeniden gondermeye kalkiyor. Yani az
        //    once basarisiz olan zincirin TAMAMI ikinci kez kosuluyor ve ayni hatalar
        //    gunluge yeniden yaziliyordu (ucuncu tekrarin kaynagi buydu).
        //
        //    OLCUT DURUM DEGIL, AZ ID'SI: EncounterSyncService tani baglama adimi varsa
        //    ONUN kaydini donduruyor (`if (diagnosisEntry is not null) return diagnosisEntry`).
        //    Yani Muayine basariyla olusup yalnizca tani baglama Update'i hata verirse donen
        //    durum Failed olur -- oysa kullanilabilir bir Encounter VARDIR ve epikriz
        //    gonderilebilir. Status'e baksaydik epikrizi haksiz yere atlardik.
        //
        //    AzResourceId tam olarak "sunucuda kullanilabilir bir Encounter var mi" sorusunu
        //    yanitliyor: yazma basarili olduysa da, tani bagligi basarisiz olduysa da dolu;
        //    atlanan (hasta yok / mapping Skipped) kayitlarda bos.
        var epikrizSayi = 0;
        if (encounter.AzResourceId is not null)
        {
            var epikriz = await compositionSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);
            epikrizSayi = epikriz.Status == SyncStatus.Success ? 1 : 0;
        }

        // 3) Laboratuvar / Radyoloji / Patoloji -- hepsi Hasta'nin AZ id'sine ihtiyac
        //    duyuyor, o yuzden Muayine gonderildikten SONRA.
        var (azPatientId, azEncounterId) = await BaglantiIdleriAsync(protokol, ct);
        if (azPatientId is null)
            return new Sonuc(encounter.Status, epikrizSayi, 0, 0, 0, 0);

        var lab = await SendAllLabsAsync(protokol, azPatientId, azEncounterId, ct);
        var rad = await SendAllRadiologyAsync(protokol, azPatientId, azEncounterId, ct);
        var pat = await SendAllPathologyAsync(protokol, azPatientId, azEncounterId, ct);
        var vital = await SendAllVitalsAsync(protokol, azPatientId, azEncounterId, ct);

        return new Sonuc(encounter.Status, epikrizSayi, lab, rad, pat, vital);
    }

    private async Task<(string? AzPatientId, string? AzEncounterId)> BaglantiIdleriAsync(
        ProtokolListItem protokol, CancellationToken ct)
    {
        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId], ct);
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        // Gunlukte yazan AZ id'nin sunucuda HALA durdugunu dogrula -- aradan bir Delete
        // gecmis olabilir; olmayan bir Encounter'a referans veren kaynak HTTP 409 alir.
        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
            if (!check.Success) azEncounterId = null;
        }

        return (azPatientId, azEncounterId);
    }

    private static bool BasariylaGonderildi(SyncLogEntry? durum) =>
        durum is { Status: SyncStatus.Success } && durum.Operation != SyncOperation.Delete;

    // BAKANLIK ISTEGI (2026-09-16, madde 4): ates gibi klinik olcumler az-observation
    // ile gonderilmeli. Kaynak Bulgulari metni; bir muayeneden birden fazla Observation
    // cikiyor (bkz. VitalSignsSyncService).
    private async Task<int> SendAllVitalsAsync(
        ProtokolListItem protokol, string azPatientId, string? azEncounterId, CancellationToken ct)
    {
        // atlaGonderilmisse: Lab/Radyoloji/Patoloji'deki BasariylaGonderildi kontrolunun
        // vital karsiligi. Burada degil servisin icinde, cunku karar muayenenin degisim
        // tarihine bakiyor ve o kayit zaten orada okunuyor (ikinci bir sorgu acmamak icin).
        var sonuclar = await vitalSignsSyncService.SyncAllAsync(
            protokol, azPatientId, azEncounterId, liveMode: true, ct, atlaGonderilmisse: true);
        return sonuclar.Count(r => r.Status == SyncStatus.Success);
    }

    private async Task<int> SendAllLabsAsync(
        ProtokolListItem protokol, string azPatientId, string? azEncounterId, CancellationToken ct)
    {
        // BAKANLIK ISTEGI (2026-09-16): gonderim birimi satir degil GRUP -- alt
        // parametreli panel tek Observation + component[]. Gruplama LabGroupBuilder'da,
        // ekrandaki gruplamayla AYNI kodda (bkz. o dosyadaki not).
        var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(protokol.ProtokolId, ct);
        var gruplar = LabGroupBuilder.Build(labs);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "Observation", gruplar.Select(g => g.AnahtarId).ToList(), ct);

        int n = 0;
        foreach (var grup in gruplar)
        {
            if (BasariylaGonderildi(durumlar.GetValueOrDefault(grup.AnahtarId))) continue;
            var r = await labResultSyncService.SyncGroupAsync(grup, protokol, azPatientId, azEncounterId, liveMode: true, ct);
            if (r.Status == SyncStatus.Success) n++;
        }
        return n;
    }

    private async Task<int> SendAllRadiologyAsync(
        ProtokolListItem protokol, string azPatientId, string? azEncounterId, CancellationToken ct)
    {
        if (!await settings.GetBoolAsync(SettingsStore.RadiologyReportSendEnabledKey, true, ct)) return 0;

        var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "DiagnosticReport", reports.Select(r => r.TetkikIslemId).ToList(), ct);

        // TOPLU OKUMA (2026-09-28 inceleme): Procedure ve Practitioner durumlari eskiden
        // dongunun ICINDE, rapor basina birer sorguyla okunuyordu -- klasik N+1. 10 raporlu
        // bir protokol 20 ayri SQLite sorgusu aciyordu. Artik dongu oncesi tek sorguda.
        var islemDurumlari = await syncLog.GetLatestByPusulaIdsAsync(
            "Procedure", reports.Select(r => r.ProtokolIslemId).ToList(), ct);
        var doktorDurumlari = await syncLog.GetLatestByPusulaIdsAsync(
            "Practitioner",
            reports.Where(r => r.RaporuOnaylayanDoktorId is not null)
                   .Select(r => r.RaporuOnaylayanDoktorId!.Value).ToList(), ct);

        int n = 0;
        foreach (var report in reports)
        {
            if (BasariylaGonderildi(durumlar.GetValueOrDefault(report.TetkikIslemId))) continue;

            var azProcedureId = CanliAzId(islemDurumlari, report.ProtokolIslemId);
            var azPractitionerId = report.RaporuOnaylayanDoktorId is { } doktorId
                ? CanliAzId(doktorDurumlari, doktorId)
                : null;

            var r = await radiologyReportSyncService.SyncOneAsync(
                report, protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
            if (r.Status == SyncStatus.Success) n++;
        }
        return n;
    }

    // Gunlukteki son kayit BASARILI ve bir AZ id'si varsa onu doner, yoksa null.
    private static string? CanliAzId(Dictionary<int, SyncLogEntry> durumlar, int pusulaId) =>
        durumlar.GetValueOrDefault(pusulaId) is { Status: SyncStatus.Success, AzResourceId: not null } kayit
            ? kayit.AzResourceId
            : null;

    private async Task<int> SendAllPathologyAsync(
        ProtokolListItem protokol, string azPatientId, string? azEncounterId, CancellationToken ct)
    {
        if (!await settings.GetBoolAsync(SettingsStore.PathologyReportSendEnabledKey, true, ct)) return 0;

        var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "DiagnosticReport-Patoloji", reports.Select(r => r.ResultId).ToList(), ct);

        // Radyolojideki ile ayni N+1 duzeltmesi -- bkz. SendAllRadiologyAsync.
        var islemDurumlari = await syncLog.GetLatestByPusulaIdsAsync(
            "Procedure",
            reports.Where(r => r.ProtokolIslemId is not null)
                   .Select(r => r.ProtokolIslemId!.Value).ToList(), ct);
        var doktorDurumlari = await syncLog.GetLatestByPusulaIdsAsync(
            "Practitioner",
            reports.Where(r => r.ApprovedById is not null)
                   .Select(r => r.ApprovedById!.Value).ToList(), ct);

        int n = 0;
        foreach (var report in reports)
        {
            if (BasariylaGonderildi(durumlar.GetValueOrDefault(report.ResultId))) continue;

            var azProcedureId = report.ProtokolIslemId is { } islemId
                ? CanliAzId(islemDurumlari, islemId)
                : null;
            var azPractitionerId = report.ApprovedById is { } doktorId
                ? CanliAzId(doktorDurumlari, doktorId)
                : null;

            var r = await pathologyReportSyncService.SyncOneAsync(
                report, protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
            if (r.Status == SyncStatus.Success) n++;
        }
        return n;
    }
}
