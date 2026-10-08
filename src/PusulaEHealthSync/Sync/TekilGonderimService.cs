using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// TEK BIR SYNCLOG KAYDINI YENIDEN GONDERME -- tur ne olursa olsun.
//
// NEDEN AYRI BIR SERVIS (2026-10-08): bu mantik Detail.cshtml.cs'te, sayfanin ICINDE
// duruyordu. Kullanici ayni islemi Aktivite Akisi'ndan da istedi ("tum silme ve gonderme
// prosesleri olsun, tek tek, toplu, hasta bazli, hepsi"). Ikinci bir kopya cikarmak bu
// projede daha once pahaliya mal oldu: 2026-09-15'te toplu gonderimin kendi zinciri vardi
// ve EncounterSyncService'ten ayristi -- sonucta epikriz, laboratuvar ve patoloji toplu
// gonderimde HIC gitmedi, canli veride yakalandi. O yuzden mantik tek yerde duruyor ve
// her iki sayfa da burayi cagiriyor.
//
// KARAR (2026-08-20'den devir): her gonderim CANLI (liveMode: true) -- "Tekrar gonder"
// diyen kullanici $validate degil gercek gonderim bekliyor.
public class TekilGonderimService(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    EHealthClient eHealthClient,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    PractitionerSyncService practitionerSyncService,
    CompositionSyncService compositionSyncService,
    ConditionSyncService conditionSyncService,
    ProcedureSyncService procedureSyncService,
    LabResultSyncService labResultSyncService,
    RadiologyReportSyncService radiologyReportSyncService,
    PathologyReportSyncService pathologyReportSyncService)
{
    // Kayit: olusan yeni SyncLog satiri. Hata: gonderilemediyse KULLANICIYA gosterilecek
    // sebep. Ikisi de null olamaz -- ya gonderildi ya da neden gonderilemedigi biliniyor.
    public record Sonuc(SyncLogEntry? Kayit, string? Hata);

    // protokolId: Tani/Islem/Laboratuvar/Radyoloji/Patoloji turlerinin gonderimi Encounter
    // baglamina (azPatientId/azEncounterId) ihtiyac duyuyor ve o baglam ancak protokol
    // biliniyorsa cozulebiliyor. Bilinmiyorsa TAHMIN EDILMIYOR -- kullaniciya neden
    // gonderilemedigi soyleniyor.
    public async Task<Sonuc> GonderAsync(SyncLogEntry kaynak, int? protokolId, CancellationToken ct = default)
    {
        switch (kaynak.ResourceType)
        {
            // Protokol baglami GEREKMEYEN turler: PusulaId tek basina yeterli.
            case "Patient":
                return new Sonuc(await patientSyncService.SyncOneAsync(kaynak.PusulaId, liveMode: true, ct), null);
            case "Encounter":
                return new Sonuc(await encounterSyncService.SyncOneAsync(kaynak.PusulaId, liveMode: true, ct), null);
            case "Practitioner":
                return new Sonuc(await practitionerSyncService.SyncOneAsync(kaynak.PusulaId, liveMode: true, ct), null);
            case "Composition":
                return new Sonuc(await compositionSyncService.SyncOneAsync(kaynak.PusulaId, liveMode: true, ct), null);

            case "Condition":
                {
                    var ctx = await BaglamCozAsync(protokolId, cascadeEncounter: true, ct);
                    if (ctx is not ({ } protokol, { } azPatientId, { } azEncounterId, _))
                        return new Sonuc(null, ctx.Sebep);
                    var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(protokol.ProtokolId, ct);
                    var tani = tanilar.FirstOrDefault(t => t.Id == kaynak.PusulaId);
                    if (tani is null) return new Sonuc(null, KaynakYok);
                    return new Sonuc(await conditionSyncService.SyncOneAsync(
                        tani, protokol, azPatientId, azEncounterId, tanilar.Count, liveMode: true, ct), null);
                }

            case "Procedure":
                {
                    var ctx = await BaglamCozAsync(protokolId, cascadeEncounter: true, ct);
                    if (ctx is not ({ } protokol, { } azPatientId, { } azEncounterId, _))
                        return new Sonuc(null, ctx.Sebep);
                    var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(protokol.ProtokolId, ct);
                    var islem = islemler.FirstOrDefault(i => i.Id == kaynak.PusulaId);
                    if (islem is null) return new Sonuc(null, KaynakYok);
                    return new Sonuc(await procedureSyncService.SyncOneAsync(
                        islem, protokol, azPatientId, azEncounterId, liveMode: true, ct), null);
                }

            case "Observation":
                {
                    var ctx = await BaglamCozAsync(protokolId, cascadeEncounter: false, ct);
                    if (ctx is not ({ } protokol, { } azPatientId, _, _))
                        return new Sonuc(null, ctx.Sebep);
                    // SyncLog.PusulaId GRUBUN anahtar satirinin Id'si olabilir de olmayabilir
                    // de (uye satirlarin da kendi kaydi var, bkz. LabResultSyncService) --
                    // ikisi de ayni panele goturuyor, panelin TAMAMI yeniden gonderiliyor.
                    var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(protokol.ProtokolId, ct);
                    var grup = LabGroupBuilder.Build(labs)
                        .FirstOrDefault(g => g.AnahtarId == kaynak.PusulaId
                                          || g.TumSatirlar.Any(l => l.LabaratuarSonucId == kaynak.PusulaId));
                    if (grup is null) return new Sonuc(null, KaynakYok);
                    return new Sonuc(await labResultSyncService.SyncGroupAsync(
                        grup, protokol, azPatientId, ctx.AzEncounterId, liveMode: true, ct), null);
                }

            case "DiagnosticReport":
                {
                    var ctx = await BaglamCozAsync(protokolId, cascadeEncounter: false, ct);
                    if (ctx is not ({ } protokol, { } azPatientId, _, _))
                        return new Sonuc(null, ctx.Sebep);
                    var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
                    var report = reports.FirstOrDefault(r => r.TetkikIslemId == kaynak.PusulaId);
                    if (report is null) return new Sonuc(null, KaynakYok);
                    var azProcedureId = await AzIdAsync("Procedure", report.ProtokolIslemId, ct);
                    var azPractitionerId = report.RaporuOnaylayanDoktorId is { } doktorId
                        ? await AzIdAsync("Practitioner", doktorId, ct) : null;
                    return new Sonuc(await radiologyReportSyncService.SyncOneAsync(
                        report, protokol, azPatientId, ctx.AzEncounterId, azProcedureId, azPractitionerId,
                        liveMode: true, ct), null);
                }

            // Zincirin UC halkasi da ayni yerden tetikleniyor: patoloji gonderimi zaten
            // Observation -> Composition -> DiagnosticReport'u BIRLIKTE gonderiyor
            // (PathologyReportSyncService), dolayisiyla hangi halkanin uzerinde "Gonder"e
            // basilirsa basilsin dogru davranis butun zinciri rapordan bastan calistirmak.
            case "DiagnosticReport-Patoloji":
            case "Composition-Patoloji":
            case "Observation-Patoloji":
                {
                    var ctx = await BaglamCozAsync(protokolId, cascadeEncounter: false, ct);
                    if (ctx is not ({ } protokol, { } azPatientId, _, _))
                        return new Sonuc(null, ctx.Sebep);

                    // Composition'in PusulaId'si rapor ile ayni (ResultId); Observation'inki
                    // ise EPulse.IslemReferansNumarasi -- once rapora cikilmasi gerekiyor.
                    var resultId = kaynak.ResourceType == "Observation-Patoloji"
                        ? await pusulaRepository.GetPathologyResultIdByIslemReferansAsync(kaynak.PusulaId, ct)
                        : kaynak.PusulaId;
                    if (resultId is null)
                        return new Sonuc(null, "Bu bulgunun ait olduğu patoloji raporu Pusula'da bulunamadı.");

                    var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(protokol.ProtokolId, ct);
                    var report = reports.FirstOrDefault(r => r.ResultId == resultId);
                    if (report is null) return new Sonuc(null, KaynakYok);
                    var azProcedureId = report.ProtokolIslemId is { } patolojiIslemId
                        ? await AzIdAsync("Procedure", patolojiIslemId, ct) : null;
                    var azPractitionerId = report.ApprovedById is { } doktorId
                        ? await AzIdAsync("Practitioner", doktorId, ct) : null;
                    return new Sonuc(await pathologyReportSyncService.SyncOneAsync(
                        report, protokol, azPatientId, ctx.AzEncounterId, azProcedureId, azPractitionerId,
                        liveMode: true, ct), null);
                }

            default:
                return new Sonuc(null,
                    $"'{SyncLogEntry.ResourceTypeLabel(kaynak.ResourceType)}' kayıt türü için tekrar gönderim henüz desteklenmiyor.");
        }
    }

    private const string KaynakYok = "Kaynak Pusula kaydı artık bulunamıyor.";

    // Basariyla gonderilmis bir kaydin AZ id'si; gonderilmemisse null (referans eklenmez).
    private async Task<string?> AzIdAsync(string resourceType, int pusulaId, CancellationToken ct)
    {
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(resourceType, [pusulaId], ct);
        return durumlar.GetValueOrDefault(pusulaId) is { Status: SyncStatus.Success, AzResourceId: not null } kayit
            ? kayit.AzResourceId
            : null;
    }

    // Tani/Islem/Laboratuvar/Radyoloji/Patoloji'nin ortak baglam ihtiyaci -- Protokol
    // Detay'daki GetGercekIdleriAsync/GetIdleriLabIcinAsync ile AYNI kurallar (canli Patient
    // aramasi, kayitli Encounter'in GERCEKTEN gecerli olup olmadigini canli dogrulama).
    //
    // cascadeEncounter=true olan turlerde (Tani/Islem -- Encounter referansi 1..1 zorunlu)
    // Encounter yoksa/gecersizse OTOMATIK yeniden gonderilir. false olanlarda (Lab/Radyoloji,
    // encounter opsiyonel) sadece referans eklenmez, YENI bir Encounter olusturulmaz.
    private async Task<(ProtokolListItem? Protokol, string? AzPatientId, string? AzEncounterId, string? Sebep)>
        BaglamCozAsync(int? protokolId, bool cascadeEncounter, CancellationToken ct)
    {
        if (protokolId is null)
            return (null, null, null, "Bu kayıt için protokol bağlamı bilinmiyor -- Protokol Detay sayfasından gönderin.");

        var protokol = await pusulaRepository.GetProtokolByIdAsync(protokolId.Value, ct);
        if (protokol is null)
            return (null, null, null, "İlgili protokol artık bulunamıyor.");

        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId], ct);
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        // SAKLANAN ID'YE KORU KORUNE GUVENILMIYOR: kayit silinmis ya da baska ortama ait
        // olabilir. Canli kontrol basarisizsa referans yokmus gibi davraniliyor.
        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
            if (!check.Success) azEncounterId = null;
        }

        if (azEncounterId is null && cascadeEncounter)
        {
            var encResult = await encounterSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);
            azEncounterId = encResult.AzResourceId;
        }

        if (azPatientId is null)
            return (null, null, null, "Hasta TRƏS'te bulunamadı -- önce Hasta gönderilmeli.");
        if (cascadeEncounter && azEncounterId is null)
            return (null, null, null, "Müayinə TRƏS'e gönderilemedi -- önce onu Protokol Detay sayfasından gönderin.");

        return (protokol, azPatientId, azEncounterId, null);
    }
}
