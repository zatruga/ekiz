using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

public class DetailModel(
    SyncLogStore syncLog,
    PusulaRepository pusulaRepository,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    PractitionerSyncService practitionerSyncService,
    CompositionSyncService compositionSyncService,
    ConditionSyncService conditionSyncService,
    ProcedureSyncService procedureSyncService,
    LabResultSyncService labResultSyncService,
    RadiologyReportSyncService radiologyReportSyncService,
    PathologyReportSyncService pathologyReportSyncService,
    DeleteService deleteService,
    EHealthClient eHealthClient) : PageModel
{
    public SyncLogEntry? Entry { get; set; }
    public string? PrettyRequest { get; set; }
    public string? PrettyResponse { get; set; }
    public string? ResendMessage { get; set; }

    // Kayit Detayi -> ilgili Protokol. Encounter/Composition icin PusulaId DOGRUDAN
    // ProtokolId'dir (birebir); Patient/Practitioner icin PusulaId bir Hasta/Doktor Id'si --
    // birden fazla protokolu olabilecegi icin EN SON protokol gosterilir (bkz.
    // PusulaRepository.GetMostRecentProtokolByHastaIdAsync/DoktorIdAsync, KULLANICI ISTEGI
    // 2026-08-21: "protokole dön butonu hepsinde olsun, hasta/protokol/bölüm adları da").
    //
    // DUZELTME (2026-08-21, canli olayda bulundu): "en son protokol" tahmini YANLIS
    // protokole goturebiliyordu -- kullanici Rauf'un protokolundeki bir Doktor kaydina
    // girip "Protokole dön" tikladiginda, o doktorun BASKA (daha yeni) bir protokolu varsa
    // oraya (baska bir HASTAYA) goturuluyordu. Cozum: Protokol.cshtml'deki "Detay" linkleri
    // artik hangi protokolden gelindigini fromProtokol route degeriyle ACIKCA tasiyor --
    // bu doluysa DOGRUDAN o protokol kullanilir (tahmin yok), sadece dolu degilse (orn.
    // Aktivite Akisi'ndan gelindiyse) eski "en son protokol" sezgisine dusulur.
    [BindProperty(SupportsGet = true)]
    public int? FromProtokol { get; set; }

    public ProtokolListItem? RelatedProtokol { get; set; }

    // "Gönderdiğim veri gerçekten doğru mu / doğru yere mi gitti" kontrolu -- kullanicinin
    // acikca istedigi bir dogrulama ekrani (bkz. konusma, 2026-08-20). ResponseJson sadece
    // o anki denemenin donen govdesini tutar; bu ise SORGU ANINDA bakanlikta GERCEKTE ne
    // var onu canli okur (aradan baska bir degisiklik gecmis olabilir). Otomatik degil,
    // "Canlı Kontrol Et" ile bilincli tetiklenir -- her sayfa acilisinda bakanliga bosuna
    // istek atilmasin diye.
    [BindProperty(SupportsGet = true)]
    public bool Verify { get; set; }

    public bool VerifyAttempted { get; set; }
    public bool VerifySuccess { get; set; }
    public int? VerifyStatusCode { get; set; }
    public string? PrettyLiveData { get; set; }

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        Entry = await syncLog.GetByIdAsync(id);
        if (Entry is null) return NotFound();
        PrettyRequest = Pretty(Entry.RequestJson);
        PrettyResponse = Pretty(Entry.ResponseJson);

        RelatedProtokol = FromProtokol is not null
            ? await pusulaRepository.GetProtokolByIdAsync(FromProtokol.Value, ct)
            : Entry.ResourceType switch
            {
                "Encounter" or "Composition" => await pusulaRepository.GetProtokolByIdAsync(Entry.PusulaId, ct),
                "Patient" => await pusulaRepository.GetMostRecentProtokolByHastaIdAsync(Entry.PusulaId, ct),
                "Practitioner" => await pusulaRepository.GetMostRecentProtokolByDoktorIdAsync(Entry.PusulaId, ct),
                _ => null,
            };

        if (Verify && Entry.AzResourceId is not null)
        {
            VerifyAttempted = true;
            var result = await eHealthClient.GetAsync(SyncLogEntry.FhirResourceType(Entry.ResourceType), Entry.AzResourceId, ct);
            VerifySuccess = result.Success;
            VerifyStatusCode = result.StatusCode;
            PrettyLiveData = Pretty(result.Body);
        }

        return Page();
    }

    // "Tekrar gonder" -- Patient, Encounter, Practitioner, Composition (Epikriz) icin
    // destekleniyor (Lab/DiagnosticReport mapper'i henuz yazilmadi -- veri kaynagi
    // netlesmedi, bkz. SettingsStore.LabOnlyVerifiedKey yorumu).
    // KARAR (2026-08-20): artik CANLI gonderim yapar (liveMode:true) -- Encounter icin
    // hasta e-Health'te yoksa EncounterSyncService onu otomatik once canli gonderir.
    public async Task<IActionResult> OnPostResendAsync(long id)
    {
        var existing = await syncLog.GetByIdAsync(id);
        if (existing is null) return NotFound();

        switch (existing.ResourceType)
        {
            case "Patient":
                {
                    var result = await patientSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            case "Encounter":
                {
                    var result = await encounterSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            case "Practitioner":
                {
                    var result = await practitionerSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            case "Composition":
                {
                    var result = await compositionSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            // Condition/Procedure/Observation/DiagnosticReport -- KULLANICI ISTEGI (2026-08-31):
            // "detay ekranına geçince gönder ve sil vs hiç bir buton yok". Bu 4 turun Protokol
            // Detay sayfasinda ZATEN kendi Gönder butonlari var (OnPostGonderTaniAsync vb.), ama
            // buraya (orn. Aktivite Akisi'ndan) dogrudan gelindiginde hicbir secenek yoktu.
            // Patient/Encounter/Practitioner/Composition'in aksine bunlarin gonderimi Encounter
            // baglamina (azPatientId/azEncounterId) ihtiyac duyuyor -- bu baglam SADECE
            // FromProtokol doluysa bilinir (Protokol sayfasindaki Detay linkleri bunu her zaman
            // tasir, bkz. Detail.cshtml.cs OnGetAsync'teki ayni gerekce). Bos ise TAHMIN
            // ETMIYORUZ, kullaniciyi Protokol sayfasina yonlendiren bir mesaj gosteriyoruz.
            case "Condition":
                {
                    var ctx = await ResolveEncounterContextAsync(cascadeEncounter: true);
                    if (ctx is not ({ } protokol, { } azPatientId, { } azEncounterId, _))
                        return await NotSupportedPage(existing, ctx.Reason!);
                    var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(protokol.ProtokolId);
                    var tani = tanilar.FirstOrDefault(t => t.Id == existing.PusulaId);
                    if (tani is null) return await NotSupportedPage(existing, "Kaynak Pusula kaydı artık bulunamıyor.");
                    var result = await conditionSyncService.SyncOneAsync(tani, protokol, azPatientId, azEncounterId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id, fromProtokol = FromProtokol });
                }
            case "Procedure":
                {
                    var ctx = await ResolveEncounterContextAsync(cascadeEncounter: true);
                    if (ctx is not ({ } protokol, { } azPatientId, { } azEncounterId, _))
                        return await NotSupportedPage(existing, ctx.Reason!);
                    var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(protokol.ProtokolId);
                    var islem = islemler.FirstOrDefault(i => i.Id == existing.PusulaId);
                    if (islem is null) return await NotSupportedPage(existing, "Kaynak Pusula kaydı artık bulunamıyor.");
                    var result = await procedureSyncService.SyncOneAsync(islem, protokol, azPatientId, azEncounterId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id, fromProtokol = FromProtokol });
                }
            case "Observation":
                {
                    var ctx = await ResolveEncounterContextAsync(cascadeEncounter: false);
                    if (ctx is not ({ } protokol, { } azPatientId, _, _))
                        return await NotSupportedPage(existing, ctx.Reason!);
                    var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(protokol.ProtokolId);
                    var lab = labs.FirstOrDefault(l => l.LabaratuarSonucId == existing.PusulaId);
                    if (lab is null) return await NotSupportedPage(existing, "Kaynak Pusula kaydı artık bulunamıyor.");
                    var result = await labResultSyncService.SyncOneAsync(lab, protokol, azPatientId, ctx.AzEncounterId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id, fromProtokol = FromProtokol });
                }
            case "DiagnosticReport":
                {
                    var ctx = await ResolveEncounterContextAsync(cascadeEncounter: false);
                    if (ctx is not ({ } protokol, { } azPatientId, _, _))
                        return await NotSupportedPage(existing, ctx.Reason!);
                    var reports = await pusulaRepository.GetRadiologyReportsByProtokolIdAsync(protokol.ProtokolId);
                    var report = reports.FirstOrDefault(r => r.TetkikIslemId == existing.PusulaId);
                    if (report is null) return await NotSupportedPage(existing, "Kaynak Pusula kaydı artık bulunamıyor.");
                    var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", [report.ProtokolIslemId]);
                    var azProcedureId = procedureStatuses.GetValueOrDefault(report.ProtokolIslemId) is { Status: SyncStatus.Success, AzResourceId: not null } proc ? proc.AzResourceId : null;
                    string? azPractitionerId = null;
                    if (report.RaporuOnaylayanDoktorId is { } doktorId)
                    {
                        var practitionerStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId]);
                        azPractitionerId = practitionerStatuses.GetValueOrDefault(doktorId) is { Status: SyncStatus.Success, AzResourceId: not null } prac ? prac.AzResourceId : null;
                    }
                    var result = await radiologyReportSyncService.SyncOneAsync(report, protokol, azPatientId, ctx.AzEncounterId, azProcedureId, azPractitionerId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id, fromProtokol = FromProtokol });
                }
            case "DiagnosticReport-Patoloji":
                {
                    var ctx = await ResolveEncounterContextAsync(cascadeEncounter: false);
                    if (ctx is not ({ } protokol, { } azPatientId, _, _))
                        return await NotSupportedPage(existing, ctx.Reason!);
                    var reports = await pusulaRepository.GetPathologyReportsByProtokolIdAsync(protokol.ProtokolId);
                    var report = reports.FirstOrDefault(r => r.ResultId == existing.PusulaId);
                    if (report is null) return await NotSupportedPage(existing, "Kaynak Pusula kaydı artık bulunamıyor.");
                    string? azProcedureId = null;
                    if (report.ProtokolIslemId is { } patolojiIslemId)
                    {
                        var procedureStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", [patolojiIslemId]);
                        azProcedureId = procedureStatuses.GetValueOrDefault(patolojiIslemId) is { Status: SyncStatus.Success, AzResourceId: not null } proc ? proc.AzResourceId : null;
                    }
                    string? azPractitionerId = null;
                    if (report.ApprovedById is { } doktorId)
                    {
                        var practitionerStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId]);
                        azPractitionerId = practitionerStatuses.GetValueOrDefault(doktorId) is { Status: SyncStatus.Success, AzResourceId: not null } prac ? prac.AzResourceId : null;
                    }
                    var result = await pathologyReportSyncService.SyncOneAsync(report, protokol, azPatientId, ctx.AzEncounterId, azProcedureId, azPractitionerId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id, fromProtokol = FromProtokol });
                }
            default:
                ResendMessage = $"'{existing.ResourceType}' kayıt türü için tekrar gönderim henüz desteklenmiyor.";
                Entry = existing;
                PrettyRequest = Pretty(existing.RequestJson);
                PrettyResponse = Pretty(existing.ResponseJson);
                return Page();
        }
    }

    private async Task<IActionResult> NotSupportedPage(SyncLogEntry existing, string reason)
    {
        ResendMessage = reason;
        Entry = existing;
        PrettyRequest = Pretty(existing.RequestJson);
        PrettyResponse = Pretty(existing.ResponseJson);
        return Page();
    }

    // Condition/Procedure/Observation/DiagnosticReport'un ortak baglam ihtiyaci -- Protokol.cshtml.cs'teki
    // GetGercekIdleriAsync/GetIdleriLabIcinAsync ile AYNI kurallar (canli Patient aramasi, kayitli
    // Encounter'in GERCEKTEN gecerli olup olmadigini canli kontrol etme). cascadeEncounter=true
    // olan turlerde (Condition/Procedure, Encounter.encounter 1..1 zorunlu) Encounter yoksa/gecersizse
    // OTOMATIK olarak yeniden gonderilir -- cascadeEncounter=false olanlarda (Lab/Radyoloji,
    // encounter opsiyonel) sadece referans eklenmez, YENI bir Encounter olusturulmaz.
    private async Task<(ProtokolListItem? Protokol, string? AzPatientId, string? AzEncounterId, string? Reason)> ResolveEncounterContextAsync(bool cascadeEncounter)
    {
        if (FromProtokol is null)
            return (null, null, null, "Bu kayıt için protokol bağlamı bilinmiyor -- Protokol Detay sayfasından tekrar gönderin.");

        var protokol = await pusulaRepository.GetProtokolByIdAsync(FromProtokol.Value);
        if (protokol is null)
            return (null, null, null, "İlgili protokol artık bulunamıyor.");

        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString());
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId]);
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId);
            if (!check.Success) azEncounterId = null;
        }

        if (azEncounterId is null && cascadeEncounter)
        {
            var encResult = await encounterSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true);
            azEncounterId = encResult.AzResourceId;
        }

        if (azPatientId is null)
            return (null, null, null, "Hasta e-Health'te bulunamadı -- önce Hasta gönderilmeli.");
        if (cascadeEncounter && azEncounterId is null)
            return (null, null, null, "Müayinə e-Health'e gönderilemedi -- önce onu Protokol Detay sayfasından gönderin.");

        return (protokol, azPatientId, azEncounterId, null);
    }

    // Yanlislikla gonderilmis bir kaydi e-Health'ten geri almak icin -- sadece gercekten
    // olusturulmus/guncellenmis (AzResourceId dolu) kayitlar silinebilir.
    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var existing = await syncLog.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var result = await deleteService.DeleteAsync(existing);
        return RedirectToPage("/Detail", new { id = result.Id });
    }

    private static string? Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            return node?.ToJsonString(PusulaEHealthSync.JsonDefaults.Indented);
        }
        catch
        {
            return json;
        }
    }
}
