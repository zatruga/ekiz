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
    // ZATEN GONDERILMIS VE DEGISMEMISSE YENIDEN GONDERME (2026-10-13).
    //
    // KULLANICI: "neden bunlari surekli surekli update olarak gonderiyor sistem?"
    //
    // SEBEP: protokol zincirinde islem, tani, tetkik, radyoloji ve patoloji icin
    // "zaten basariyla gonderildiyse atla" kontrolu VARDI (2026-10-08'de eklendi,
    // bkz. ProtocolFullSyncService.BasariylaGonderildi) -- ama protokolun UC OMURGA
    // kaydinda yoktu: Hasta, Muayine, Epikriz. Protokol her islendiginde ucu de
    // kosulsuz Update olarak gidiyordu.
    //
    // Acik bir protokole gun boyunca yeni tetkik/islem geldikce protokol hakli olarak
    // listeye giriyor ve omurga her seferinde tekrar yollaniyor. Olculdu (13.10, canli):
    // bir endokrinoloji protokolunde omurga 11 KEZ gonderilmis -- o gun protokole dort
    // kez yeni kalem gelmis. Ortalama 1,2-1,3 kez; bedeli bakanliga gereksiz Update
    // trafigi ve HTTP 429 (hiz siniri).
    //
    // "GONDERILDI, BIR DAHA BAKMA" DEGIL, "DEGISMEDIYSE BAKMA": hasta kaydi duzeltilir
    // (FIN, ad), epikriz sonradan yazilir. Olcut Pusula'daki son degisim ani; gonderimden
    // SONRAYSA yeniden gonderiliyor. Ayni desen vital bulgularda zaten var
    // (VitalSignsSyncService.DegismedenGonderilmisMiAsync).
    //
    // YALNIZCA OTOMATIK YOL ATLAR: ekrandaki "Gonder" dugmesi varsayilan false gecer --
    // kullanici acikca bastiysa zorlamali.
    public async Task<SyncLogEntry> SyncOneAsync(
        int hastaId, bool liveMode, CancellationToken ct = default, bool atlaGonderilmisse = false)
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

        if (atlaGonderilmisse && await DegismedenGonderilmisMiAsync(hasta, ct) is { } oncekiKayit)
            return oncekiKayit;

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

    // Son basarili gonderimden beri degismemisse O KAYDI doner (cagiran taraf
    // AzResourceId'ye ihtiyac duyabiliyor), degismisse null -- gonderim surer.
    //
    // Son kayit BASARISIZSA atlanmaz: hata gecici olabilir. Silme sonrasi da atlanmaz
    // (Operation=Delete, kayit TRƏS'te yok demektir).
    private async Task<SyncLogEntry?> DegismedenGonderilmisMiAsync(HastaRecord hasta, CancellationToken ct)
    {
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "Patient", [hasta.Id], ct, govdeleriGetir: false);

        if (durumlar.GetValueOrDefault(hasta.Id) is not
            { Status: SyncStatus.Success, Operation: not SyncOperation.Delete } son) return null;
        if (son.AzResourceId is null) return null;

        // Pusula tarihleri YEREL (Baki), SyncLog UTC saklar -- cevrim sart. Cevrilmeseydi
        // dort saatlik fark her kaydi "gonderimden sonra degismis" gosterir ve duzeltme
        // hicbir ise yaramazdi (ayni tuzak VitalSignsSyncService'te de yazili).
        //
        // ModifiedDate canli veride %57 NULL (hic degistirilmemis hastalar) -- o zaman
        // olcut kaydin olusturulma ani.
        var degisim = hasta.ModifiedDate ?? hasta.CreatedDate;
        if (degisim is null) return null;              // tarih yoksa guvenli taraf: gonder
        return AzTime.ToUtc(degisim.Value) <= son.CreatedAtUtc ? son : null;
    }
}

