using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir patoloji raporunu e-Health'e gonderir. azProcedureId ve azPractitionerId cagiran
// taraftan (EncounterSyncService / sayfalar) hazir gelir -- bu servis kendi basina
// Procedure/Practitioner cascade'i YAPMAZ.
//
// V2 (2026-09-08) -- artik UC HALKALI bir zincir gonderiyor:
//     Observation(lar) (az-pathology-finding)   -- kodlanmis ICD-O-3 bulgu
//         -> Composition (az-pathology-report-composition)  -- bulgulara referans verir
//             -> DiagnosticReport (az-pathology-diagnostic-report)  -- Composition'a referans verir
//
// SIRA TERSTEN KURULU VE BU BILEREK: her halka bir ustune referans verdigi icin once
// bulgular, sonra Composition, EN SON DiagnosticReport gonderilir. Boylece DiagnosticReport
// tek yazimda tamamlanir -- once ekstensiyonsuz gonderip sonra guncelleme (iki yazim)
// gerekmiyor.
//
// GERIYE DONUK UYUMLULUK: SyncOneAsync'in imzasi ve donus degeri DEGISMEDI (hala
// DiagnosticReport'un SyncLogEntry'sini dondurur) -- dort cagiran yerin (EncounterSyncService,
// Detail, Protokol x2) hicbiri degismek zorunda kalmadi. Bulgusu olmayan raporlarda zincir
// hic kurulmaz ve davranis v1 ile BIREBIR ayni kalir.
public class PathologyReportSyncService(
    EHealthClient eHealthClient,
    PusulaRepository repository,
    SyncLogStore syncLog,
    ILogger<PathologyReportSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(
        PathologyReportRecord report, ProtokolListItem protokol, string azPatientId, string? azEncounterId,
        string? azProcedureId, string? azPractitionerId, bool liveMode, CancellationToken ct = default)
    {
        // 1) Kodlanmis bulgular. Neoplazi olmayan raporlarda (canli veride %75) bu liste BOS
        //    doner -- sorgu "0000/x" satirlarini zaten eliyor (gerekce:
        //    GetPathologyFindingsByResultIdAsync). Bos liste bir hata degil, normal yol.
        var (azCompositionId, chainWarning) = await SyncChainAsync(report, protokol, azPatientId, azEncounterId, azPractitionerId, liveMode, ct);

        // 2) DiagnosticReport -- zincir kurulabildiyse ust halkayi da tasiyarak.
        var mapping = PathologyReportMapper.Map(report, azPatientId, azEncounterId, azProcedureId, azPractitionerId, azCompositionId);
        if (mapping is MappingResult.Skipped skip)
        {
            var skipEntry = NewEntry(report, protokol, SyncStatus.Skipped);
            skipEntry.Message = skip.Reason;
            await syncLog.InsertAsync(skipEntry, ct);
            return skipEntry;
        }

        var diagnosticReport = ((MappingResult.Success)mapping).Resource;
        var localId = PathologyReportMapper.LocalUniqueId(report.ResultId);
        // Zincirde bir sey ters gittiyse bu, raporun KENDI gonderimini engellemez ama
        // rapor kaydinin mesajina yazilir. Aksi halde Protokol sayfasinda rapor YESIL
        // "Basarili" gorunurdu -- oysa bulgular gitmemis olurdu; sessiz ve yaniltici bir durum.
        var entry = await SendAsync("DiagnosticReport", diagnosticReport, localId, liveMode,
            status => NewEntry(report, protokol, status), ct, chainWarning);

        if (entry.Status == SyncStatus.Failed)
            logger.LogWarning("Patoloji raporu gonderilemedi (ProtokolId={ProtokolId}, ResultId={Id}): {Message}", protokol.ProtokolId, report.ResultId, entry.Message);

        return entry;
    }

    // Observation'lari ve Composition'i gonderir, Composition'in AZ id'sini dondurur
    // (kurulamadiysa null -- o zaman DiagnosticReport v1'deki gibi tek basina gider).
    private async Task<(string? CompositionId, string? Warning)> SyncChainAsync(
        PathologyReportRecord report, ProtokolListItem protokol, string azPatientId, string? azEncounterId,
        string? azPractitionerId, bool liveMode, CancellationToken ct)
    {
        var findings = await repository.GetPathologyFindingsByResultIdAsync(report.ResultId, ct);
        if (findings.Count == 0)
            return (null, null);   // neoplazi yok -- normal yol, uyari degil

        var failedFindings = 0;
        var azFindingIds = new List<string>();
        foreach (var finding in findings)
        {
            var mapping = PathologyFindingMapper.Map(finding, azPatientId, azEncounterId, azPractitionerId);
            if (mapping is MappingResult.Skipped skip)
            {
                // Buraya sadece GERCEK veri sorunlari duser (bozuk ICD-O-3 bicimi ya da
                // tabloda karsiligi olmayan yerlesim yeri kodu) -- "neoplazi yok" normal
                // durumu SQL'de elendigi icin gunlugu doldurmaz.
                failedFindings++;
                var skipEntry = NewFindingEntry(finding, protokol, SyncStatus.Skipped);
                skipEntry.Message = skip.Reason;
                await syncLog.InsertAsync(skipEntry, ct);
                continue;
            }

            // Note dolu ise (yerlesim yeri tabloda yok -> Pusula tablosu ya da C80.9
            // "bilinmeyen birincil bolge" kullanildi, veya morfolojiden derece eki
            // ayiklandi) gonderim ENGELLENMEZ ama neden SyncLog'a yazilir -- boylece hangi
            // kayitlarin eslestirme tablosuna eklenmesi gerektigi gunlukten gorulebilir.
            var findingSuccess = (MappingResult.Success)mapping;
            var observation = findingSuccess.Resource;
            var localId = PathologyFindingMapper.LocalUniqueId(finding.IslemReferansNumarasi);
            var entry = await SendAsync("Observation", observation, localId, liveMode,
                status => NewFindingEntry(finding, protokol, status), ct, findingSuccess.Note);

            if (entry.Status != SyncStatus.Success)
            {
                failedFindings++;
                logger.LogWarning("Patoloji bulgusu gonderilemedi (ResultId={Id}, IslemRef={Ref}): {Message}", report.ResultId, finding.IslemReferansNumarasi, entry.Message);
                continue;
            }

            // Dogrulama (test) modunda sunucu bir kayit OLUSTURMADIGI icin gercek bir id yok.
            // Composition'in section.entry'si bos kalmasin diye yerel id kullaniliyor --
            // $validate referansin var olup olmadigina bakmaz, yapiyi dogrular. Canli modda
            // ise sunucunun dondurdugu gercek id kullanilir.
            azFindingIds.Add(liveMode ? entry.AzResourceId ?? localId : localId);
        }

        if (azFindingIds.Count == 0)
            return (null, $"DİKKAT: bu raporun {findings.Count} kodlanmış bulgusunun hiçbiri gönderilemedi -- rapor yalnızca serbest metin olarak gitti, ICD-O-3 bulgu bilgisi e-Health'te YOK. Ayrıntı için Aktivite akışında \"Patoloji Bulgusu\" kayıtlarına bakın.");

        var compositionMapping = PathologyCompositionMapper.Map(report, azFindingIds, azPatientId, azEncounterId, azPractitionerId);
        if (compositionMapping is MappingResult.Skipped compositionSkip)
        {
            var skipEntry = NewCompositionEntry(report, protokol, SyncStatus.Skipped);
            skipEntry.Message = compositionSkip.Reason;
            await syncLog.InsertAsync(skipEntry, ct);
            return (null, $"DİKKAT: patoloji belgesi (Composition) oluşturulamadı -- {compositionSkip.Reason}");
        }

        var composition = ((MappingResult.Success)compositionMapping).Resource;
        var compositionLocalId = PathologyCompositionMapper.LocalUniqueId(report.ResultId);
        var compositionEntry = await SendAsync("Composition", composition, compositionLocalId, liveMode,
            status => NewCompositionEntry(report, protokol, status), ct);

        if (compositionEntry.Status != SyncStatus.Success)
        {
            logger.LogWarning("Patoloji Composition'i gonderilemedi (ResultId={Id}): {Message}", report.ResultId, compositionEntry.Message);
            return (null, "DİKKAT: bulgular gönderildi ama onları bir arada tutan patoloji belgesi (Composition) gönderilemedi -- rapor bulgulara bağlanamadı.");
        }

        var partial = failedFindings > 0
            ? $"DİKKAT: {findings.Count} bulgudan {failedFindings} tanesi gönderilemedi, kalanı gönderildi."
            : null;
        return (liveMode ? compositionEntry.AzResourceId : compositionLocalId, partial);
    }

    // Uc kaynak tipinin de ortak gonderim akisi: test modunda $validate, canli modda
    // local-system-unique-id ile once arayip Create/Update. Onceden bu blok sadece
    // DiagnosticReport icin vardi ve satir satir yaziliydi; v2'de ayni akis Observation ve
    // Composition icin de gerektiginden ortaklastirildi.
    private async Task<SyncLogEntry> SendAsync(
        string resourceType, JsonObject resource, string localId, bool liveMode,
        Func<SyncStatus, SyncLogEntry> newEntry, CancellationToken ct, string? note = null)
    {
        var requestJson = resource.ToJsonString(JsonDefaults.Options);

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync(resourceType, resource, ct);
            var validateEntry = newEntry(validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            validateEntry.Operation = SyncOperation.Validate;
            validateEntry.Message = CombineMessage(validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body), note);
            validateEntry.RequestJson = requestJson;
            validateEntry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(validateEntry, ct);
            return validateEntry;
        }

        var existingId = await eHealthClient.FindExistingIdAsync(resourceType, localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync(resourceType, resource, ct)
            : await eHealthClient.UpdateAsync(resourceType, existingId, resource, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = newEntry(writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = CombineMessage(writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body), note);
        writeEntry.RequestJson = requestJson;
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);
        return writeEntry;
    }

    // EncounterSyncService.CombineMessage ile ayni kalip: hata mesaji ve "dikkat cekmesi
    // gereken ama gonderimi engellemeyen" not ayni alanda birlestirilir.
    private static string? CombineMessage(string? primary, string? note)
    {
        if (primary is null) return note;
        if (note is null) return primary;
        return $"{primary} -- {note}";
    }

    // ONEMLI: ResourceType burada BILEREK "DiagnosticReport" DEGIL -- Radyoloji de ayni FHIR
    // kaynagina (DiagnosticReport) gidiyor ama TetkikIslemId/ResultId BAGIMSIZ, ORTUSEN ID
    // uzaylari (bkz. PathologyReportMapper.LocalUniqueId). SyncLog (ResourceType, PusulaId)
    // ikilisiyle anahtarlandigi icin ayni etiketi paylasmak yanlis durum eslesmesine yol acardi.
    private static SyncLogEntry NewEntry(PathologyReportRecord report, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "DiagnosticReport-Patoloji",
        PusulaId = report.ResultId,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };

    // Ayni gerekce: Laboratuvar sonuclari da Observation olarak gidiyor ve iki ID uzayi
    // bagimsiz -- ayri etiket sart. PusulaId = IslemReferansNumarasi (EPulse satiri basina
    // benzersiz, canli veride dogrulandi).
    private static SyncLogEntry NewFindingEntry(PathologyFindingRecord finding, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "Observation-Patoloji",
        PusulaId = finding.IslemReferansNumarasi,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };

    // Ayni gerekce: Epikriz de Composition olarak gidiyor (PusulaId = ProtokolId), burada
    // PusulaId = ResultId -- ayni sayiya denk gelebilecekleri icin etiket ayri.
    private static SyncLogEntry NewCompositionEntry(PathologyReportRecord report, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "Composition-Patoloji",
        PusulaId = report.ResultId,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
