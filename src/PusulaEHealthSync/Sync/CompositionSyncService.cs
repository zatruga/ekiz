using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Epikriz (Composition, profile: az-discharge-summary) senkronu -- EncounterSyncService ile
// ayni kalip, ama bagimlilik zinciri bir kademe daha uzun: Composition.encounter (1..1) ve
// Composition.author (1..* -- Encounter'in aksine OPSIYONEL DEGIL) zorunlu. Bu yuzden
// Encounter VE Practitioner ikisi de e-Health'te yoksa (liveMode=true ise) burada otomatik
// once gonderilir; Practitioner basarisiz olursa (Encounter'daki gibi "participant bos
// birak" degil) Composition tumden SKIPPED olur -- author'suz gecerli bir Composition yok.
public class CompositionSyncService(
    PusulaRepository repository,
    EHealthClient eHealthClient,
    SyncLogStore syncLog,
    SettingsStore settings,
    EncounterSyncService encounterSyncService,
    PractitionerSyncService practitionerSyncService,
    ILogger<CompositionSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(int protokolId, bool liveMode, CancellationToken ct = default)
    {
        var protokol = await repository.GetProtokolByIdAsync(protokolId, ct);
        if (protokol is null)
        {
            var missing = new SyncLogEntry
            {
                ResourceType = "Composition",
                PusulaId = protokolId,
                Status = SyncStatus.Failed,
                Message = "Pusula'da bu Id ile protokol bulunamadi",
            };
            await syncLog.InsertAsync(missing, ct);
            return missing;
        }

        var hasta = await repository.GetHastaByIdAsync(protokol.HastaId, ct);

        // KULLANICI ISTEGI (2026-08-21): Reçete protokolleri hiç gönderilmesin. EncounterMapper.Map
        // da ayni kontrolu yapiyor, ama Composition Encounter'i BULAMAZSA (yani e-Health'te YOKSA)
        // cascade ile ona ugrar -- Encounter zaten varsa (orn. bu kural eklenmeden ONCE gonderilmis
        // eski bir kayitsa) bu kontrole hic dokunmadan gecebilirdi. Composition'in kendisi de
        // Reçete protokollerde asla gonderilmemeli, bu yuzden burada AYRICA ve doğrudan kontrol
        // ediliyor -- sadece Encounter cascade'ine guvenilmiyor.
        if (protokol.ProtokolTipiId == EncounterMapper.ReceteProtokolTipiId)
        {
            var receteEntry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            receteEntry.Message = "Protokol tipi Reçete -- bu tür protokoller e-Health'e gönderilmez";
            await syncLog.InsertAsync(receteEntry, ct);
            return receteEntry;
        }

        var genelMuayene = await repository.GetGenelMuayeneByProtokolIdAsync(protokolId, ct);

        if (genelMuayene is null)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Bu protokol için muayene/epikriz kaydı bulunamadı";
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var sendEnabled = await settings.GetBoolAsync(SettingsStore.EpikrizSendEnabledKey, true, ct);
        if (!sendEnabled)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Epikriz gönderimi Ayarlar sayfasından kapatılmış";
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var onlySigned = await settings.GetBoolAsync(SettingsStore.EpikrizOnlySignedKey, true, ct);
        if (onlySigned && !genelMuayene.IsLocked)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Epikriz henüz Pusula'da kilitlenmemiş (tamamlanmamış) -- Ayarlar'dan bu kural kapatılabilir";
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        // Bagimlilik 1: Encounter -- Composition.encounter (1..1) zorunlu.
        var azEncounterId = await eHealthClient.FindExistingIdAsync("Encounter", protokolId.ToString(), ct);
        if (azEncounterId is null && liveMode)
        {
            logger.LogInformation("hasta.protokol.Id={Id}: muayene e-Health'te yok, epikriz oncesi once otomatik gonderiliyor", protokolId);
            var encounterResult = await encounterSyncService.SyncOneAsync(protokolId, liveMode: true, ct);
            azEncounterId = encounterResult.Status == SyncStatus.Success
                ? encounterResult.AzResourceId ?? await eHealthClient.FindExistingIdAsync("Encounter", protokolId.ToString(), ct)
                : null;

            if (azEncounterId is null)
            {
                var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
                entry.Message = $"Muayene otomatik gönderilemedi ({encounterResult.Status}): {encounterResult.Message ?? "sebep belirtilmedi"}";
                await syncLog.InsertAsync(entry, ct);
                return entry;
            }
        }
        else if (azEncounterId is null)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Muayene e-Health'te bulunamadi -- once Müayinə gönderilmeli (canlı Create/Update ile)";
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct);
        if (azPatientId is null)
        {
            // Encounter az'da varsa Patient'in de olmasi garanti (Encounter cascade'i onu
            // zaten once gonderir) -- yine de teorik bir tutarsizlik ihtimaline karsi kontrol.
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Hasta e-Health'te bulunamadi (beklenmeyen durum -- Müayinə var ama Hasta yok)";
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        // Bagimlilik 2: Practitioner -- Composition.author (1..*) zorunlu, Encounter.participant
        // gibi OPSIYONEL DEGIL. Basarisiz olursa Composition gonderilemez.
        var azPractitionerId = await eHealthClient.FindExistingIdAsync("Practitioner", genelMuayene.DoktorId.ToString(), ct);
        if (azPractitionerId is null && liveMode)
        {
            var practitionerResult = await practitionerSyncService.SyncOneAsync(genelMuayene.DoktorId, liveMode: true, ct);
            azPractitionerId = practitionerResult.Status == SyncStatus.Success
                ? practitionerResult.AzResourceId ?? await eHealthClient.FindExistingIdAsync("Practitioner", genelMuayene.DoktorId.ToString(), ct)
                : null;
        }

        if (azPractitionerId is null)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Epikrizi yazan doktor e-Health'te gönderilemedi -- Composition.author zorunlu olduğu için gönderim yapılamıyor";
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var mapping = CompositionMapper.Map(genelMuayene, protokol, azPatientId, azEncounterId, azPractitionerId);

        if (mapping is MappingResult.Skipped skip)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = skip.Reason;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var success = (MappingResult.Success)mapping;
        var composition = success.Resource;
        var requestJson = composition.ToJsonString(JsonDefaults.Options);

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Composition", composition, ct);
            var entry = NewEntry(protokol, hasta, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body);
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var localId = protokolId.ToString();
        var existingId = await eHealthClient.FindExistingIdAsync("Composition", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Composition", composition, ct)
            : await eHealthClient.UpdateAsync("Composition", existingId, composition, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(protokol, hasta, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body);
        writeEntry.RequestJson = composition.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);
        return writeEntry;
    }

    private static SyncLogEntry NewEntry(ProtokolListItem protokol, HastaRecord? hasta, SyncStatus status) => new()
    {
        ResourceType = "Composition",
        PusulaId = protokol.ProtokolId,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        FathersName = hasta?.BabaAdi,
        BirthDate = hasta?.DogumTarihi is null ? null : DateOnly.FromDateTime(hasta.DogumTarihi.Value),
        Gender = hasta?.CinsiyetId,
        Fin = protokol.Fin,
        RecordOpenedAt = hasta?.CreatedDate,
    };
}
