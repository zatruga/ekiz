using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir ICD tanisini Condition olarak gonderir -- EncounterSyncService'ten CASCADE
// olarak cagrilir (KULLANICI ISTEGI, 2026-08-24: "Encounter'a da tani ekleyin"), Patient/
// Practitioner ile ayni kalip. Encounter/Patient'in aksine burada "otomatik once gonder"
// YOK -- Condition.encounter (1..1) zorunlu oldugu icin cagiran taraf (EncounterSyncService)
// zaten Encounter BASARIYLA olusturulduktan/guncellendikten SONRA bunu cagirir, azEncounterId
// her zaman gecerli olur.
public class ConditionSyncService(EHealthClient eHealthClient, SyncLogStore syncLog, ILogger<ConditionSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(
        IcdTaniRecord tani, ProtokolListItem protokol, string azPatientId, string azEncounterId, bool liveMode, CancellationToken ct = default)
    {
        var mapping = ConditionMapper.Map(tani, protokol, azPatientId, azEncounterId);
        var success = (MappingResult.Success)mapping; // ConditionMapper.Map hicbir zaman Skipped donmez
        var condition = success.Resource;
        var requestJson = condition.ToJsonString(JsonDefaults.Options);
        var localId = $"{protokol.ProtokolId}-{tani.ICDId}";

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Condition", condition, ct);
            var entry = NewEntry(tani, protokol, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success
                ? tani.Kodu
                : $"{tani.Kodu}: {EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body)}";
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var existingId = await eHealthClient.FindExistingIdAsync("Condition", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Condition", condition, ct)
            : await eHealthClient.UpdateAsync("Condition", existingId, condition, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(tani, protokol, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success
            ? tani.Kodu
            : $"{tani.Kodu}: {EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body)}";
        writeEntry.RequestJson = condition.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);

        if (!writeResult.Success)
            logger.LogWarning("Condition gonderilemedi (ProtokolId={ProtokolId}, ICD={Kodu}): {Message}", protokol.ProtokolId, tani.Kodu, writeEntry.Message);

        return writeEntry;
    }

    private static SyncLogEntry NewEntry(IcdTaniRecord tani, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "Condition",
        PusulaId = tani.Id,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
