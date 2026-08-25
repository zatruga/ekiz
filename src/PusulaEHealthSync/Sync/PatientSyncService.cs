using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Tek bir hastayi map edip gonderen ve sonucu loglayan ortak mantik. Hem Worker'in
// otomatik dongusu hem de web dashboard'daki "tekrar gonder" butonu bunu kullanir --
// ayni kod iki yerde tekrarlanmasin diye.
public class PatientSyncService(
    PusulaRepository repository,
    EHealthClient eHealthClient,
    SyncLogStore syncLog,
    ILogger<PatientSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(int hastaId, bool liveMode, CancellationToken ct = default)
    {
        var hasta = await repository.GetHastaByIdAsync(hastaId, ct);
        if (hasta is null)
        {
            var missing = new SyncLogEntry
            {
                ResourceType = "Patient",
                PusulaId = hastaId,
                Status = SyncStatus.Failed,
                Message = "Pusula'da bu Id ile hasta bulunamadi",
            };
            await syncLog.InsertAsync(missing, ct);
            return missing;
        }

        var mapping = PatientMapper.Map(hasta);

        if (mapping is MappingResult.Skipped skip)
        {
            var entry = NewEntry(hasta, SyncStatus.Skipped);
            entry.Message = skip.Reason;
            await syncLog.InsertAsync(entry, ct);
            logger.LogWarning("ATLANDI hasta.hasta.Id={Id}: {Reason}", hasta.Id, skip.Reason);
            return entry;
        }

        var patient = ((MappingResult.Success)mapping).Resource;
        var requestJson = patient.ToJsonString(JsonDefaults.Options);

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Patient", patient, ct);
            var entry = NewEntry(hasta, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body);
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var localId = hasta.Id.ToString();
        var existingId = await eHealthClient.FindExistingIdAsync("Patient", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Patient", patient, ct)
            : await eHealthClient.UpdateAsync("Patient", existingId, patient, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(hasta, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body);
        // Update icin EHealthClient govdedeki id'yi sunucu id'siyle degistiriyor (bkz.
        // EHealthClient.UpdateAsync) -- gonderilen GERCEK govdeyi loglamak icin burada
        // (mutasyondan SONRA) yeniden serialize ediyoruz, erkenden alinmis requestJson'i degil.
        writeEntry.RequestJson = patient.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);
        return writeEntry;
    }

    // Hasta demografik bilgilerini (ad/soyad/baba adi/dogum tarihi/cinsiyet) her
    // sonuc turunde (basarili/atlanan/hatali) dashboard'da gosterebilmek icin
    // SyncLogEntry'ye kopyalar -- mapping basarisiz olsa bile bu bilgiler Pusula'dan
    // zaten okunmus durumda.
    private static SyncLogEntry NewEntry(HastaRecord hasta, SyncStatus status) => new()
    {
        ResourceType = "Patient",
        PusulaId = hasta.Id,
        Status = status,
        PatientFullName = string.Join(" ", new[] { hasta.Adi, hasta.Adi2, hasta.Soyadi }.Where(s => !string.IsNullOrWhiteSpace(s))),
        FathersName = hasta.BabaAdi,
        BirthDate = hasta.DogumTarihi is null ? null : DateOnly.FromDateTime(hasta.DogumTarihi.Value),
        Gender = hasta.CinsiyetId,
        Fin = hasta.TCKimlikNo,
        RecordOpenedAt = hasta.CreatedDate,
    };
}
