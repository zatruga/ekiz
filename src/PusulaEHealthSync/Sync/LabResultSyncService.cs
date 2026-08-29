using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir laboratuvar test sonucunu (LabResultRecord) Observation olarak gonderir --
// ProcedureSyncService ile ayni kalip. Procedure'in aksine Observation.encounter ZORUNLU
// DEGIL (bkz. LabResultObservationMapper) -- Muayine henuz gonderilmemis olsa bile lab
// sonucu gonderilebilir, sadece Hasta'nin (Patient) e-Health'te var olmasi yeterli.
public class LabResultSyncService(EHealthClient eHealthClient, SyncLogStore syncLog, ILogger<LabResultSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(
        LabResultRecord lab, ProtokolListItem protokol, string azPatientId, string? azEncounterId, bool liveMode, CancellationToken ct = default)
    {
        var mapping = LabResultObservationMapper.Map(lab, azPatientId, azEncounterId);
        if (mapping is MappingResult.Skipped skip)
        {
            var skipEntry = NewEntry(lab, protokol, SyncStatus.Skipped);
            skipEntry.Message = skip.Reason;
            await syncLog.InsertAsync(skipEntry, ct);
            return skipEntry;
        }

        var success = (MappingResult.Success)mapping;
        var observation = success.Resource;
        var requestJson = observation.ToJsonString(JsonDefaults.Options);
        var localId = lab.LabaratuarSonucId.ToString();

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Observation", observation, ct);
            var entry = NewEntry(lab, protokol, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body);
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var existingId = await eHealthClient.FindExistingIdAsync("Observation", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Observation", observation, ct)
            : await eHealthClient.UpdateAsync("Observation", existingId, observation, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(lab, protokol, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body);
        writeEntry.RequestJson = observation.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);

        if (!writeResult.Success)
            logger.LogWarning("Lab sonucu gonderilemedi (ProtokolId={ProtokolId}, LabaratuarSonucId={Id}): {Message}", protokol.ProtokolId, lab.LabaratuarSonucId, writeEntry.Message);

        return writeEntry;
    }

    private static SyncLogEntry NewEntry(LabResultRecord lab, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "Observation",
        PusulaId = lab.LabaratuarSonucId,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
