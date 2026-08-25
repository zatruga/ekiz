using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir ProtokolIslem kaydini Procedure olarak gonderir -- EncounterSyncService'ten
// CASCADE olarak cagrilir, ConditionSyncService ile ayni kalip. Procedure.encounter
// (1..1) zorunlu oldugu icin cagiran taraf zaten Encounter BASARIYLA yazildiktan SONRA
// bunu cagirir, azEncounterId her zaman gecerli olur. Condition'in aksine Procedure
// Encounter'a geri referans EKLEMIYOR (base FHIR Encounter'da boyle bir alan yok) --
// bu yuzden burada ikinci bir Encounter update'i gerekmiyor.
public class ProcedureSyncService(EHealthClient eHealthClient, SyncLogStore syncLog, ILogger<ProcedureSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(
        IslemRecord islem, ProtokolListItem protokol, string azPatientId, string azEncounterId, bool liveMode, CancellationToken ct = default)
    {
        var mapping = ProcedureMapper.Map(islem, azPatientId, azEncounterId);
        var success = (MappingResult.Success)mapping; // ProcedureMapper.Map hicbir zaman Skipped donmez
        var procedure = success.Resource;
        var requestJson = procedure.ToJsonString(JsonDefaults.Options);
        var localId = islem.Id.ToString();

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Procedure", procedure, ct);
            var entry = NewEntry(islem, protokol, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success
                ? islem.IcbariKodu
                : $"{islem.IcbariKodu}: {EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body)}";
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var existingId = await eHealthClient.FindExistingIdAsync("Procedure", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Procedure", procedure, ct)
            : await eHealthClient.UpdateAsync("Procedure", existingId, procedure, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(islem, protokol, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success
            ? islem.IcbariKodu
            : $"{islem.IcbariKodu}: {EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body)}";
        writeEntry.RequestJson = procedure.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);

        if (!writeResult.Success)
            logger.LogWarning("Procedure gonderilemedi (ProtokolId={ProtokolId}, Icbari={Kodu}): {Message}", protokol.ProtokolId, islem.IcbariKodu, writeEntry.Message);

        return writeEntry;
    }

    private static SyncLogEntry NewEntry(IslemRecord islem, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "Procedure",
        PusulaId = islem.Id,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
