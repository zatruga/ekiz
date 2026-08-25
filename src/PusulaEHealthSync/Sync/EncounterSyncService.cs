using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// PatientSyncService ile ayni kalip: tek bir protokolu map edip gonderen ve sonucu
// loglayan ortak mantik (Worker + web dashboard "gonder" butonu ayni kodu kullanir).
//
// KRITIK FARK: Encounter.subject, Patient'in AZ tarafindaki GERCEK FHIR id'sine referans
// vermek zorunda (1..1). KARAR (2026-08-20, kullanici istegi): bu artik kullaniciya
// sorulmuyor -- e-Health'te hasta bulunamazsa ve liveMode=true ise, Encounter'dan once
// Patient OTOMATIK OLARAK canli gonderilir (PatientSyncService.SyncOneAsync liveMode:true).
// Hasta gonderimi de basarisiz olursa (Skipped/Failed) Encounter da gonderilemez, tek
// bir SyncLogEntry'de nedeniyle birlikte loglanir. liveMode=false (validate-only) durumunda
// otomatik canli gonderim YAPILMAZ -- sadece dogrulama denemesi, kalici veri olusturmamali.
public class EncounterSyncService(
    PusulaRepository repository,
    EHealthClient eHealthClient,
    SyncLogStore syncLog,
    PatientSyncService patientSyncService,
    PractitionerSyncService practitionerSyncService,
    ConditionSyncService conditionSyncService,
    ProcedureSyncService procedureSyncService,
    BolumMappingStore bolumMappingStore,
    SettingsStore settings,
    ILogger<EncounterSyncService> logger)
{
    public async Task<SyncLogEntry> SyncOneAsync(int protokolId, bool liveMode, CancellationToken ct = default)
    {
        var protokol = await repository.GetProtokolByIdAsync(protokolId, ct);
        if (protokol is null)
        {
            var missing = new SyncLogEntry
            {
                ResourceType = "Encounter",
                PusulaId = protokolId,
                Status = SyncStatus.Failed,
                Message = "Pusula'da bu Id ile protokol bulunamadi",
            };
            await syncLog.InsertAsync(missing, ct);
            return missing;
        }

        var hasta = await repository.GetHastaByIdAsync(protokol.HastaId, ct);

        var azPatientId = await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct);
        if (azPatientId is null && liveMode)
        {
            logger.LogInformation("hasta.protokol.Id={Id}: hasta (HastaId={HastaId}) e-Health'te yok, once otomatik gonderiliyor", protokol.ProtokolId, protokol.HastaId);
            var patientResult = await patientSyncService.SyncOneAsync(protokol.HastaId, liveMode: true, ct);
            azPatientId = patientResult.Status == SyncStatus.Success
                ? patientResult.AzResourceId ?? await eHealthClient.FindExistingIdAsync("Patient", protokol.HastaId.ToString(), ct)
                : null;

            if (azPatientId is null)
            {
                var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
                entry.Message = $"Hasta otomatik gonderilemedi ({patientResult.Status}): {patientResult.Message ?? "sebep belirtilmedi"}";
                await syncLog.InsertAsync(entry, ct);
                logger.LogWarning("ATLANDI hasta.protokol.Id={Id}: otomatik hasta gonderimi basarisiz ({Status})", protokol.ProtokolId, patientResult.Status);
                return entry;
            }
        }
        else if (azPatientId is null)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = "Hasta e-Health'te bulunamadi -- once Patient gonderilmeli (canli Create/Update ile)";
            await syncLog.InsertAsync(entry, ct);
            logger.LogWarning("ATLANDI hasta.protokol.Id={Id}: hasta (HastaId={HastaId}) e-Health'te bulunamadi", protokol.ProtokolId, protokol.HastaId);
            return entry;
        }

        // Practitioner (doktor) katilimi OPSIYONEL (Encounter.participant 0..*) -- hasta'nin
        // aksine, doktor gonderimi basarisiz/atlanan olsa bile Encounter yine de gonderilir,
        // sadece participant alani bos kalir.
        //
        // KARAR (2026-08-24, bakanlik geri bildirimi -- "her Muayine'de doktor bilgisi
        // gondermeyin, bir kere gonderin, kodu yeterli"): eskiden HER Encounter'da
        // FindExistingIdAsync (uzak bir arama cagrisi) yapilip, bulunamazsa YENIDEN
        // gonderilmeye calisiliyordu -- basarisiz olan bir doktor icin (orn. gecersiz FIN
        // formati) bu, O DOKTORUN HER PROTOKOLUNDE tekrar tekrar CREATE denemesi anlamina
        // geliyordu (bakanlik loglarinda "her seferinde gonderiyorsunuz" izlenimi buradan).
        // Artik uzak aramaya HIC gidilmiyor -- SADECE kendi SyncLog gecmisimize (Practitioner,
        // DoktorId) bakiyoruz:
        //   - Daha once BASARIYLA gonderilmisse: kayitli AZ id DOGRUDAN kullanilir, tekrar
        //     gonderilmez ("gönderirse bir daha hiçbir protokolde göndermesin").
        //   - HIC denenmemisse: ilk (ve tek) canli deneme burada yapilir.
        //   - Daha once BASARISIZ olmussa: BURADA tekrar denenmez, participant bos birakilir
        //     ("hatalıysa protokolü boş bilgi ile göndersin") -- elle tekrar denemek icin
        //     bkz. Doktorlar sayfasi (kullanici: "hatalı olanı da denemesinde sıkıntı yok",
        //     yani baska bir yerden/zamanda tekrar denenebilir, sadece HER Encounter'da degil).
        string? azPractitionerId = null;
        if (protokol.DoktorId is not null)
        {
            var practitionerStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [protokol.DoktorId.Value], ct);
            var lastAttempt = practitionerStatuses.GetValueOrDefault(protokol.DoktorId.Value);

            if (lastAttempt is { Status: SyncStatus.Success, AzResourceId: not null })
            {
                azPractitionerId = lastAttempt.AzResourceId;
            }
            else if (lastAttempt is null && liveMode)
            {
                var practitionerResult = await practitionerSyncService.SyncOneAsync(protokol.DoktorId.Value, liveMode: true, ct);
                azPractitionerId = practitionerResult.Status == SyncStatus.Success ? practitionerResult.AzResourceId : null;
                if (azPractitionerId is null)
                    logger.LogInformation("hasta.protokol.Id={Id}: doktor (DoktorId={DoktorId}) ilk gonderim basarisiz ({Status}), participant bos birakilacak -- bundan sonra otomatik tekrar denenmeyecek", protokol.ProtokolId, protokol.DoktorId, practitionerResult.Status);
            }
        }

        var bolumMap = await bolumMappingStore.GetAllAsync(ct);
        var mapping = EncounterMapper.Map(protokol, azPatientId, azPractitionerId, bolumMap);

        if (mapping is MappingResult.Skipped skip)
        {
            var entry = NewEntry(protokol, hasta, SyncStatus.Skipped);
            entry.Message = skip.Reason;
            await syncLog.InsertAsync(entry, ct);
            logger.LogWarning("ATLANDI hasta.protokol.Id={Id}: {Reason}", protokol.ProtokolId, skip.Reason);
            return entry;
        }

        var success = (MappingResult.Success)mapping;
        var encounter = success.Resource;
        var requestJson = encounter.ToJsonString(JsonDefaults.Options);

        if (!liveMode)
        {
            var validateResult = await eHealthClient.ValidateAsync("Encounter", encounter, ct);
            var entry = NewEntry(protokol, hasta, validateResult.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = CombineMessage(validateResult.Success ? null : EHealthErrorFormatter.Describe(validateResult.StatusCode ?? 0, validateResult.Body), success.Note);
            entry.RequestJson = requestJson;
            entry.ResponseJson = validateResult.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var localId = protokol.ProtokolId.ToString();
        var existingId = await eHealthClient.FindExistingIdAsync("Encounter", localId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var writeResult = existingId is null
            ? await eHealthClient.CreateAsync("Encounter", encounter, ct)
            : await eHealthClient.UpdateAsync("Encounter", existingId, encounter, ct);

        string? returnedId = null;
        if (writeResult.Success && writeResult.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(writeResult.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(protokol, hasta, writeResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = CombineMessage(writeResult.Success ? null : EHealthErrorFormatter.Describe(writeResult.StatusCode ?? 0, writeResult.Body), success.Note);
        // Update icin EHealthClient govdedeki id'yi sunucu id'siyle degistiriyor (bkz.
        // EHealthClient.UpdateAsync) -- gonderilen GERCEK govdeyi loglamak icin burada
        // (mutasyondan SONRA) yeniden serialize ediyoruz, erkenden alinmis requestJson'i degil.
        writeEntry.RequestJson = encounter.ToJsonString(JsonDefaults.Options);
        writeEntry.ResponseJson = writeResult.Body;
        await syncLog.InsertAsync(writeEntry, ct);

        var azEncounterId = returnedId ?? existingId;
        if (writeResult.Success && azEncounterId is not null)
        {
            var diagnosisEntry = await SyncDiagnosesAsync(protokol, hasta, azPatientId, azPractitionerId, azEncounterId, bolumMap, ct);
            await SyncProceduresAsync(protokol, azPatientId, azEncounterId, ct);
            if (diagnosisEntry is not null) return diagnosisEntry;
        }

        return writeEntry;
    }

    // AZ Procedure -- KULLANICI KARARI (2026-08-25): "ayrım için şuanda tek gönderim
    // yapacağımız alan ICBARI SİGORTA FİYAT LİSTESİ eşleştirilmesi yapılanları göndereceğiz"
    // (bkz. PusulaRepository.GetIslemlerByProtokolIdAsync). Procedure.encounter (1..1)
    // zorunlu oldugu icin Condition ile ayni sekilde Encounter basariyla yazildiktan
    // SONRA gonderiliyor -- ama Condition'dan farkli olarak Encounter'a geri referans
    // EKLEMEDIGI icin (base FHIR Encounter'da boyle bir alan yok) ikinci bir Encounter
    // update'ine gerek yok, dolayisiyla cagiran tarafa donecek "headline" bir entry de
    // yok -- her Procedure kendi SyncLogEntry'sini kendi yazar (Aktivite Akisi'nda gorunur).
    private async Task SyncProceduresAsync(ProtokolListItem protokol, string azPatientId, string azEncounterId, CancellationToken ct)
    {
        if (!await settings.GetBoolAsync(SettingsStore.ProcedureSendEnabledKey, true, ct)) return;

        var islemler = await repository.GetIslemlerByProtokolIdAsync(protokol.ProtokolId, ct);
        foreach (var islem in islemler)
            await procedureSyncService.SyncOneAsync(islem, protokol, azPatientId, azEncounterId, liveMode: true, ct);
    }

    // Encounter.diagnosis -- KULLANICI ISTEGI (2026-08-24, bakanlik geri bildirimi):
    // "Encounter'a da tani ekleyin". Condition.encounter (1..1) zorunlu oldugu icin
    // ONCE Encounter'in GERCEK AZ id'si bilinmeli -- bu yuzden Condition'lar Encounter
    // basariyla yazildiktan SONRA gonderiliyor, ardindan Encounter (artik Condition
    // referanslariyla) IKINCI KEZ guncelleniyor. Protokolde ICD tanisi yoksa (cogu
    // protokolde henuz yok) null doner, cagiran taraf ilk (tanisiz) writeEntry'yi kullanir.
    private async Task<SyncLogEntry?> SyncDiagnosesAsync(
        ProtokolListItem protokol, HastaRecord? hasta, string azPatientId, string? azPractitionerId, string azEncounterId,
        IReadOnlyDictionary<int, string?> bolumMap, CancellationToken ct)
    {
        if (!await settings.GetBoolAsync(SettingsStore.ConditionSendEnabledKey, true, ct)) return null;

        var tanilar = await repository.GetTanilarByProtokolIdAsync(protokol.ProtokolId, ct);
        if (tanilar.Count == 0) return null;

        var conditionIds = new List<string>();
        foreach (var tani in tanilar)
        {
            var conditionResult = await conditionSyncService.SyncOneAsync(tani, protokol, azPatientId, azEncounterId, liveMode: true, ct);
            if (conditionResult.AzResourceId is not null)
                conditionIds.Add(conditionResult.AzResourceId);
        }
        if (conditionIds.Count == 0) return null;

        var diagMapping = EncounterMapper.Map(protokol, azPatientId, azPractitionerId, bolumMap, conditionIds);
        if (diagMapping is not MappingResult.Success diagSuccess) return null;

        var diagResult = await eHealthClient.UpdateAsync("Encounter", azEncounterId, diagSuccess.Resource, ct);
        var diagEntry = NewEntry(protokol, hasta, diagResult.Success ? SyncStatus.Success : SyncStatus.Failed);
        diagEntry.Operation = SyncOperation.Update;
        diagEntry.AzResourceId = azEncounterId;
        diagEntry.Message = diagResult.Success
            ? $"{conditionIds.Count} tanı bağlantısı eklendi"
            : EHealthErrorFormatter.Describe(diagResult.StatusCode ?? 0, diagResult.Body);
        diagEntry.RequestJson = diagSuccess.Resource.ToJsonString(JsonDefaults.Options);
        diagEntry.ResponseJson = diagResult.Body;
        await syncLog.InsertAsync(diagEntry, ct);
        return diagEntry;
    }

    private static string? CombineMessage(string? primary, string? note)
    {
        if (primary is null) return note;
        if (note is null) return primary;
        return $"{primary} -- {note}";
    }

    private static SyncLogEntry NewEntry(ProtokolListItem protokol, HastaRecord? hasta, SyncStatus status) => new()
    {
        ResourceType = "Encounter",
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
