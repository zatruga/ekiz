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
public class ProcedureSyncService(
    EHealthClient eHealthClient, SyncLogStore syncLog, HizmetMappingStore hizmetMappingStore,
    PractitionerSyncService practitionerSyncService,
    ILogger<ProcedureSyncService> logger)
{
    // ===== PERFORMER DOGRULAMA ONBELLEGI =====
    //
    // NEDEN GEREKLI: PerformerCozAsync her ISLEM icin calisiyor ve kayitli bir AZ id
    // varsa UZAK bir GET atiyor. Onbelleksiz bu, 01.10 sonrasi 36.335 isleme karsilik
    // 36.335 ek istek demekti -- ve sistem zaten HTTP 429 (hiz siniri) yiyor (13.10'da
    // bir tur bu yuzden birakildi). Bir doktor gun icinde onlarca islemde geciyor;
    // ayni kaydi her seferinde yeniden dogrulamak saf israf.
    //
    // Servis SINGLETON (bkz. Program.cs), bu yuzden onbellek surecin omru boyunca
    // yasar -- ConcurrentDictionary, cunku turlar es zamanli donebilir.
    //
    // TTL VAR, SINIRSIZ DEGIL: bir Practitioner TRƏS tarafinda silinebilir ve o zaman
    // referans veren Procedure "HTTP 409: Non-existent reference" ile duser. Bir saat,
    // "her islemde dogrula" ile "hic dogrulama" arasinda olculu bir orta yol.
    private static readonly TimeSpan PerformerOnbellekOmru = TimeSpan.FromHours(1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string? AzId, DateTime Zaman)>
        _performerOnbellek = new();

    public async Task<SyncLogEntry> SyncOneAsync(
        IslemRecord islem, ProtokolListItem protokol, string azPatientId, string azEncounterId, bool liveMode, CancellationToken ct = default)
    {
        var eslestirme = await hizmetMappingStore.GetMapAsync(ct);
        var azPractitionerId = liveMode ? await PerformerCozAsync(islem, ct) : null;
        // "GONDERILMEZ" ISARETLI HIZMET GUNLUGE YAZILMAZ (2026-10-28, kullanici: "gonderim
        // raporunda eksik olarak yer almasin"). Atlandi kaydi yazilsaydi Genel Bakis'in
        // tablolari ve gun sonu raporu bu islemi "eksik verili" sayardi. Kayit bellekte
        // donuyor ki cagiran zincir sonucu yine gorebilsin.
        if (eslestirme.TryGetValue(islem.HizmetId, out var karar) && karar.Gonderilmez)
        {
            var bilgi = NewEntry(islem, protokol, SyncStatus.Skipped);
            bilgi.Message = "Hizmet Eşleştirme ekranında \"gönderilmez\" işaretli -- gönderilmedi, günlüğe yazılmadı";
            return bilgi;
        }
        var mapping = ProcedureMapper.Map(islem, azPatientId, azEncounterId, eslestirme, azPractitionerId);
        if (mapping is MappingResult.Skipped atlandi)
        {
            var skipEntry = NewEntry(islem, protokol, SyncStatus.Skipped);
            skipEntry.Message = atlandi.Reason;
            await syncLog.InsertAsync(skipEntry, ct);
            return skipEntry;
        }
        var success = (MappingResult.Success)mapping;
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

    // ===== performer icin Practitioner referansi (2026-10-13) =====
    //
    // BURAYA KONDU, CAGIRAN TARAFA DEGIL: ProcedureSyncService'i IKI yer cagiriyor --
    // EncounterSyncService.SyncProceduresAsync (zincir) ve TekilGonderimService (ekrandaki
    // "Gonder" dugmesi). Cozumlemeyi cagirana birakmak ayni mantigi iki yere yazmak
    // olurdu; bu projede o desenin bedeli zaten bir kez odendi (Protokol Detay ile
    // Aktivite 2026-09-15'te ayri gonderim yolu kullandigi icin ayrismislardi).
    //
    // KURAL EncounterSyncService'teki doktor participant'i ile AYNI:
    //   1) Yalnizca KENDI SyncLog gecmisimize bakilir (uzak arama yok).
    //   2) Kayitli bir AZ id varsa GET ile DOGRULANIR -- bayat bir id, referans VEREN
    //      kaydi "HTTP 409: Non-existent reference" ile komple dusururdu.
    //   3) Hic denenmemisse bir kez gonderilir.
    //   4) Her sey basarisizsa null doner; Procedure performer'siz gider (0..*).
    //      Bir referans ugruna islemin kendisini kaybetmek dogru olmazdi.
    private async Task<string?> PerformerCozAsync(IslemRecord islem, CancellationToken ct)
    {
        if (islem.UygulayanPersonelId is not { } personelId) return null;

        if (_performerOnbellek.TryGetValue(personelId, out var onbellek)
            && DateTime.UtcNow - onbellek.Zaman < PerformerOnbellekOmru)
            return onbellek.AzId;

        var azId = await PerformerUzaktanCozAsync(islem, personelId, ct);
        _performerOnbellek[personelId] = (azId, DateTime.UtcNow);
        return azId;
    }

    // Onbellege DUSMEYEN yol -- asil is burada.
    private async Task<string?> PerformerUzaktanCozAsync(IslemRecord islem, int personelId, CancellationToken ct)
    {
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [personelId], ct);
        var son = durumlar.GetValueOrDefault(personelId);

        if (son is { Status: SyncStatus.Success, AzResourceId: not null })
        {
            var kontrol = await eHealthClient.GetAsync("Practitioner", son.AzResourceId, ct);
            if (kontrol.Success) return son.AzResourceId;

            logger.LogInformation(
                "ProtokolIslem.Id={IslemId}: islemi yapan personelin (PersonelId={PersonelId}) "
                + "kayitli AZ id'si artik gecerli degil, yeniden gonderiliyor",
                islem.Id, personelId);
        }
        else if (son is not null)
        {
            // Onceki deneme basarisiz ya da atlanmis (orn. klinik olmayan personel tipi,
            // FIN bicimi bozuk). BURADA TEKRAR DENENMIYOR -- ayni personel gun icinde
            // onlarca islemde geciyor, her birinde yeniden denemek bakanliga gereksiz
            // trafik yaratirdi. Gunluk takilan taramasi bu kayitlari zaten yeniden deniyor.
            return null;
        }

        var sonuc = await practitionerSyncService.SyncOneAsync(personelId, liveMode: true, ct);
        return sonuc.Status == SyncStatus.Success ? sonuc.AzResourceId : null;
    }

    private static SyncLogEntry NewEntry(IslemRecord islem, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = "Procedure",
        PusulaId = islem.Id,
        Status = status,
        ProtokolId = protokol.ProtokolId,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
