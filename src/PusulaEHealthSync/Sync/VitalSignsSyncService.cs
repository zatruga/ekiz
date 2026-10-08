using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Vital bulgu (az-observation) senkronu -- BAKANLIK ISTEGI (2026-09-16, madde 4).
//
// Kaynak Tedavi.GenelMuayene.Bulgulari; ayristirma VitalBulguParser'da (neden metinden
// okundugu orada anlatiliyor -- vital icin ayrilmis uc yapisal alan da bos).
//
// Bir muayeneden BIRDEN FAZLA Observation cikar (ates, nabiz, kan basinci, SpO2, boy,
// kilo, VKI, VYA), bu yuzden ConditionSyncService gibi "tek kayit" kalibi degil, kendi
// icinde donen bir SyncAllAsync var. Senkron gunlugunde hepsi ResourceType
// "Observation-Vital" ve PusulaId = GenelMuayene.Id ile yazilir; laboratuvarin
// "Observation" kayitlariyla karismasin diye ayri bir tur adi kullaniliyor (patoloji
// icin "DiagnosticReport-Patoloji" ile ayni kalip).
public class VitalSignsSyncService(
    PusulaRepository repository,
    EHealthClient eHealthClient,
    SyncLogStore syncLog,
    ILogger<VitalSignsSyncService> logger)
{
    public const string ResourceTypeAdi = "Observation-Vital";

    // atlaGonderilmisse: ZATEN BASARIYLA GONDERILMIS ve o gunden beri DEGISMEMIS vitaller
    // yeniden gonderilmez. Otomatik dongu true gecer; ekrandaki "Gönder" dugmesi false
    // (varsayilan) -- kullanici acikca bastiysa zorlamali.
    //
    // NEDEN EKLENDI (2026-10-08, CANLI VERIDE OLCULDU): bu metot kosulsuz gonderiyordu ve
    // otomatik dongu her turda protokolun TUM vitallerini yeniden yolluyordu. Sunucudaki
    // senkron gunlugunde tek bir vital kaydi 344 KEZ gonderilmis gorunuyor -- 344'u de
    // basarili, yani 343'u tamamen gereksiz. Laboratuvar, radyoloji ve patolojide bu kontrol
    // (ProtocolFullSyncService.BasariylaGonderildi) vardi; yalnizca vitalde unutulmus.
    //
    // Bedeli uc kat: bakanliga gereksiz Update trafigi, sisen senkron gunlugu (239.000
    // satirin buyuk kismi bu), ve en onemlisi YAVASLAYAN TUR -- her tur ayni vitalleri
    // yeniden yolladigi icin saatte daha az protokol gonderilebiliyordu.
    //
    // DEGISIKLIK KONTROLU TARIHE GORE: "gonderildi, bir daha bakma" demek yanlis olurdu --
    // muayeneye sonradan yeni olcum eklenebiliyor. Muayenenin son degisim ani gonderimden
    // SONRAYSA yeniden gonderiliyor.
    public async Task<List<SyncLogEntry>> SyncAllAsync(
        ProtokolListItem protokol, string azPatientId, string? azEncounterId, bool liveMode,
        CancellationToken ct = default, bool atlaGonderilmisse = false)
    {
        var muayene = await repository.GetGenelMuayeneByProtokolIdAsync(protokol.ProtokolId, ct);
        if (muayene is null) return [];

        if (atlaGonderilmisse && await DegismedenGonderilmisMiAsync(muayene, ct)) return [];

        var kaynaklar = VitalSignsMapper.Map(muayene, protokol, azPatientId, azEncounterId);
        if (kaynaklar.Count == 0) return [];

        var sonuclar = new List<SyncLogEntry>();
        foreach (var kaynak in kaynaklar)
            sonuclar.Add(await GonderAsync(kaynak, muayene, protokol, liveMode, ct));

        return sonuclar;
    }

    // Bir protokolun TUM vitalleri ayni PusulaId ile (GenelMuayene.Id) yaziliyor, yani
    // gunlukteki son kayit kumenin tamamini temsil ediyor. Son kayit basariliysa ve
    // muayene o andan beri degismediyse gonderilecek yeni bir sey yok.
    //
    // Son kayit BASARISIZSA atlanmaz: hata gecici olabilir, bir sonraki tur yeniden dener.
    // Silme sonrasi da atlanmaz -- Operation=Delete, kaydin TRƏS'te olmadigi anlamina gelir.
    private async Task<bool> DegismedenGonderilmisMiAsync(GenelMuayeneRecord muayene, CancellationToken ct)
    {
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            ResourceTypeAdi, [muayene.Id], ct, govdeleriGetir: false);

        if (durumlar.GetValueOrDefault(muayene.Id) is not
            { Status: SyncStatus.Success, Operation: not SyncOperation.Delete } son) return false;

        // Pusula tarihleri YEREL (Baki), SyncLog UTC saklar -- cevrim sart. Cevrilmeseydi
        // dort saatlik fark yuzunden her muayene "gonderimden sonra degismis" gorunur ve
        // duzeltme hicbir ise yaramazdi.
        var degisim = AzTime.ToUtc(muayene.ModifiedDate ?? muayene.CreatedDate);
        return degisim <= son.CreatedAtUtc;
    }

    private async Task<SyncLogEntry> GonderAsync(
        VitalSignsMapper.VitalKaynak kaynak, GenelMuayeneRecord muayene, ProtokolListItem protokol,
        bool liveMode, CancellationToken ct)
    {
        var requestJson = kaynak.Resource.ToJsonString(JsonDefaults.Options);

        if (!liveMode)
        {
            var validate = await eHealthClient.ValidateAsync("Observation", kaynak.Resource, ct);
            var entry = NewEntry(muayene, protokol, validate.Success ? SyncStatus.Success : SyncStatus.Failed);
            entry.Operation = SyncOperation.Validate;
            entry.Message = validate.Success
                ? kaynak.Ad
                : $"{kaynak.Ad}: {EHealthErrorFormatter.Describe(validate.StatusCode ?? 0, validate.Body)}";
            entry.RequestJson = requestJson;
            entry.ResponseJson = validate.Body;
            await syncLog.InsertAsync(entry, ct);
            return entry;
        }

        var existingId = await eHealthClient.FindExistingIdAsync("Observation", kaynak.LocalId, ct);
        var operation = existingId is null ? SyncOperation.Create : SyncOperation.Update;
        var write = existingId is null
            ? await eHealthClient.CreateAsync("Observation", kaynak.Resource, ct)
            : await eHealthClient.UpdateAsync("Observation", existingId, kaynak.Resource, ct);

        string? returnedId = null;
        if (write.Success && write.Body is not null)
        {
            try { returnedId = System.Text.Json.Nodes.JsonNode.Parse(write.Body)?["id"]?.GetValue<string>(); }
            catch { /* onemli degil, ham yanit zaten loglaniyor */ }
        }

        var writeEntry = NewEntry(muayene, protokol, write.Success ? SyncStatus.Success : SyncStatus.Failed);
        writeEntry.Operation = operation;
        writeEntry.AzResourceId = returnedId ?? existingId;
        writeEntry.Message = write.Success
            ? kaynak.Ad
            : $"{kaynak.Ad}: {EHealthErrorFormatter.Describe(write.StatusCode ?? 0, write.Body)}";
        writeEntry.RequestJson = requestJson;
        writeEntry.ResponseJson = write.Body;
        await syncLog.InsertAsync(writeEntry, ct);

        if (!write.Success)
            logger.LogWarning("Vital bulgu gonderilemedi (ProtokolId={ProtokolId}, {Ad}): {Message}",
                protokol.ProtokolId, kaynak.Ad, writeEntry.Message);

        return writeEntry;
    }

    private static SyncLogEntry NewEntry(GenelMuayeneRecord muayene, ProtokolListItem protokol, SyncStatus status) => new()
    {
        ResourceType = ResourceTypeAdi,
        PusulaId = muayene.Id,
        Status = status,
        PatientFullName = protokol.HastaAdiSoyadi,
        Fin = protokol.Fin,
    };
}
