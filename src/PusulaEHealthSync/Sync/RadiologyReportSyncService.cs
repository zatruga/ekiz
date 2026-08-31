using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir radyoloji raporunu (RadiologyReportRecord) DiagnosticReport olarak gonderir --
// LabResultSyncService ile ayni kalip. azProcedureId ve azPractitionerId cagiran taraftan
// (EncounterSyncService) hazir gelir -- bu servis kendi basina Procedure/Practitioner
// cascade'i YAPMAZ (Procedure zaten ayni Encounter cagrisi icinde SyncProceduresAsync
// tarafindan onceden gonderilmis olur, Practitioner icin de Encounter'daki participant
// doktoruyla AYNI "sadece SyncLog'daki gecmise bak, basarisizsa tekrar deneme" kurali
// cagiran tarafta uygulanir).
public class RadiologyReportSyncService(EHealthClient eHealthClient, SyncLogStore syncLog, ILogger<RadiologyReportSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(
        RadiologyReportRecord report, ProtokolListItem protokol, string azPatientId, string? azEncounterId,
        string? azProcedureId, string? azPractitionerId, bool liveMode, CancellationToken ct = default)
    {
        var mapping = RadiologyReportMapper.Map(report, azPatientId, azEncounterId, azProcedureId, azPractitionerId);
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
        var localId = report.TetkikIslemId.ToString();

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
            logger.LogWarning("Radyoloji raporu gonderilemedi (ProtokolId={ProtokolId}, TetkikIslemId={Id}): {Message}", protokol.ProtokolId, report.TetkikIslemId, writeEntry.Message);

        return writeEntry;
    }

    private static SyncLogEntry NewEntry(RadiologyReportRecord report, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "DiagnosticReport",
        PusulaId = report.TetkikIslemId,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
