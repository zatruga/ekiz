using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir patoloji raporunu (PathologyReportRecord) DiagnosticReport olarak gonderir --
// RadiologyReportSyncService ile BIREBIR AYNI kalip. azProcedureId ve azPractitionerId
// cagiran taraftan (EncounterSyncService) hazir gelir -- bu servis kendi basina
// Procedure/Practitioner cascade'i YAPMAZ.
public class PathologyReportSyncService(EHealthClient eHealthClient, SyncLogStore syncLog, ILogger<PathologyReportSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(
        PathologyReportRecord report, ProtokolListItem protokol, string azPatientId, string? azEncounterId,
        string? azProcedureId, string? azPractitionerId, bool liveMode, CancellationToken ct = default)
    {
        var mapping = PathologyReportMapper.Map(report, azPatientId, azEncounterId, azProcedureId, azPractitionerId);
        if (mapping is MappingResult.Skipped skip)
        {
            var skipEntry = NewEntry(report, protokol, SyncStatus.Skipped);
            skipEntry.Message = skip.Reason;
            await syncLog.InsertAsync(skipEntry, ct);
            return skipEntry;
        }

        var success = (MappingResult.Success)mapping;
        var diagnosticReport = success.Resource;
        var requestJson = diagnosticReport.ToJsonString(JsonDefaults.Options);
        var localId = PathologyReportMapper.LocalUniqueId(report.ResultId);

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("DiagnosticReport", diagnosticReport, ct);
            var entry = NewEntry(report, protokol, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body);
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var existingId = await eHealthClient.FindExistingIdAsync("DiagnosticReport", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("DiagnosticReport", diagnosticReport, ct)
            : await eHealthClient.UpdateAsync("DiagnosticReport", existingId, diagnosticReport, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(report, protokol, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body);
        writeEntry.RequestJson = diagnosticReport.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);

        if (!writeResult.Success)
            logger.LogWarning("Patoloji raporu gonderilemedi (ProtokolId={ProtokolId}, ResultId={Id}): {Message}", protokol.ProtokolId, report.ResultId, writeEntry.Message);

        return writeEntry;
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
}
