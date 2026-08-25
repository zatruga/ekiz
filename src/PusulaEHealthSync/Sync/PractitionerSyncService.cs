using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// PatientSyncService ile ayni kalip -- tek bir doktoru (IK.Personel) map edip gonderen ve
// sonucu loglayan ortak mantik. EncounterSyncService bunu, Encounter.participant icin
// hasta cascade'iyle AYNI mantikla (e-Health'te yoksa ve liveMode=true ise once bunu
// otomatik canli gonderir) cagirir.
public class PractitionerSyncService(
    PusulaRepository repository,
    EHealthClient eHealthClient,
    SyncLogStore syncLog,
    ILogger<PractitionerSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(int personelId, bool liveMode, CancellationToken ct = default)
    {
        var personel = await repository.GetPersonelByIdAsync(personelId, ct);
        if (personel is null)
        {
            var missing = new SyncLogEntry
            {
                ResourceType = "Practitioner",
                PusulaId = personelId,
                Status = SyncStatus.Failed,
                Message = "Pusula'da bu Id ile personel bulunamadi",
            };
            await syncLog.InsertAsync(missing, ct);
            return missing;
        }

        var mapping = PractitionerMapper.Map(personel);

        if (mapping is MappingResult.Skipped skip)
        {
            var entry = NewEntry(personel, SyncStatus.Skipped);
            entry.Message = skip.Reason;
            await syncLog.InsertAsync(entry, ct);
            logger.LogWarning("ATLANDI IK.Personel.Id={Id}: {Reason}", personel.Id, skip.Reason);
            return entry;
        }

        var practitioner = ((MappingResult.Success)mapping).Resource;
        var requestJson = practitioner.ToJsonString(JsonDefaults.Options);

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Practitioner", practitioner, ct);
            var entry = NewEntry(personel, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body);
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var localId = personel.Id.ToString();
        var existingId = await eHealthClient.FindExistingIdAsync("Practitioner", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Practitioner", practitioner, ct)
            : await eHealthClient.UpdateAsync("Practitioner", existingId, practitioner, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(personel, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body);
        writeEntry.RequestJson = practitioner.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);
        return writeEntry;
    }

    private static SyncLogEntry NewEntry(PersonelRecord personel, SyncStatus status) => new()
    {
        ResourceType = "Practitioner",
        PusulaId = personel.Id,
        Status = status,
        PatientFullName = string.Join(" ", new[] { personel.Adi, personel.Soyadi }.Where(s => !string.IsNullOrWhiteSpace(s))),
        Fin = personel.TCKimlikNo,
    };
}
