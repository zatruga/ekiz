using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Bir protokolun BUTUN e-Health gonderimini tek yerde toplar: Hasta -> Muayine ->
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
public class ProtocolFullSyncService(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    EHealthClient eHealthClient,
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
        // Recete protokolleri e-Health'e hic gonderilmiyor (Protokol Detay'daki
        // butonlarin tamami da bu durumda gizli).
        if (protokol.ProtokolTipiId == EncounterMapper.ReceteProtokolTipiId)
            return Sonuc.Bos(SyncStatus.Skipped);

        // 1) Hasta -> Muayine -> Tani -> Islem (cascade EncounterSyncService'te)
        var encounter = await encounterSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);

        // 2) Epikriz -- cascade'e DAHIL DEGIL, ayrica cagrilmali.
        var epikriz = await compositionSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);
        var epikrizSayi = epikriz.Status == SyncStatus.Success ? 1 : 0;

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
        var sonuclar = await vitalSignsSyncService.SyncAllAsync(
            protokol, azPatientId, azEncounterId, liveMode: true, ct);
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
        var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "DiagnosticReport", reports.Select(r => r.TetkikIslemId).ToList(), ct);

        int n = 0;
        foreach (var report in reports)
        {
            if (BasariylaGonderildi(durumlar.GetValueOrDefault(report.TetkikIslemId))) continue;

            var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", [report.ProtokolIslemId], ct);
            var azProcedureId = procedureStatuses.GetValueOrDefault(report.ProtokolIslemId) is
                { Status: SyncStatus.Success, AzResourceId: not null } proc ? proc.AzResourceId : null;

            string? azPractitionerId = null;
            if (report.RaporuOnaylayanDoktorId is { } doktorId)
            {
                var pracStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId], ct);
                azPractitionerId = pracStatuses.GetValueOrDefault(doktorId) is
                    { Status: SyncStatus.Success, AzResourceId: not null } prac ? prac.AzResourceId : null;
            }

            var r = await radiologyReportSyncService.SyncOneAsync(
                report, protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
            if (r.Status == SyncStatus.Success) n++;
        }
        return n;
    }

    private async Task<int> SendAllPathologyAsync(
        ProtokolListItem protokol, string azPatientId, string? azEncounterId, CancellationToken ct)
    {
        var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "DiagnosticReport-Patoloji", reports.Select(r => r.ResultId).ToList(), ct);

        int n = 0;
        foreach (var report in reports)
        {
            if (BasariylaGonderildi(durumlar.GetValueOrDefault(report.ResultId))) continue;

            string? azProcedureId = null;
            if (report.ProtokolIslemId is { } islemId)
            {
                var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", [islemId], ct);
                azProcedureId = procedureStatuses.GetValueOrDefault(islemId) is
                    { Status: SyncStatus.Success, AzResourceId: not null } proc ? proc.AzResourceId : null;
            }

            string? azPractitionerId = null;
            if (report.ApprovedById is { } doktorId)
            {
                var pracStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId], ct);
                azPractitionerId = pracStatuses.GetValueOrDefault(doktorId) is
                    { Status: SyncStatus.Success, AzResourceId: not null } prac ? prac.AzResourceId : null;
            }

            var r = await pathologyReportSyncService.SyncOneAsync(
                report, protokol, azPatientId, azEncounterId, azProcedureId, azPractitionerId, liveMode: true, ct);
            if (r.Status == SyncStatus.Success) n++;
        }
        return n;
    }
}
