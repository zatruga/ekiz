using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Yanlislikla (veya test amacli) gonderilmis bir kaydi e-Health'ten geri almak icin --
// kullanicinin acikca istedigi bir guvenlik agi (bkz. konusma: "yanlislikla gonderilen
// verinin silinmesi var mi"). Sadece gercekten olusturulmus/guncellenmis (AzResourceId
// dolu) kayitlar silinebilir -- sadece $validate edilmis bir kaydin silinecek bir seyi yok.
public class DeleteService(EHealthClient eHealthClient, SyncLogStore syncLog, ILogger<DeleteService> logger)
{
    public async Task<SyncLogEntry> DeleteAsync(SyncLogEntry source, CancellationToken ct = default)
    {
        if (source.AzResourceId is null)
        {
            var missing = CloneAsNew(source, SyncStatus.Failed);
            missing.Message = "Silinecek bir e-Health kaydı yok (bu kayıt sadece doğrulanmış, hiç oluşturulmamış)";
            await syncLog.InsertAsync(missing, ct);
            return missing;
        }

        var result = await eHealthClient.DeleteAsync(SyncLogEntry.FhirResourceType(source.ResourceType), source.AzResourceId, ct);
        var entry = CloneAsNew(source, result.Success ? SyncStatus.Success : SyncStatus.Failed);
        entry.AzResourceId = source.AzResourceId;
        entry.Message = result.Success
            ? $"e-Health'ten silindi ({source.ResourceType}/{source.AzResourceId})"
            : EHealthErrorFormatter.Describe(result.StatusCode ?? 0, result.Body);
        entry.ResponseJson = result.Body;
        await syncLog.InsertAsync(entry, ct);

        if (result.Success)
            logger.LogWarning("SILINDI {ResourceType}/{AzId} (PusulaId={PusulaId})", source.ResourceType, source.AzResourceId, source.PusulaId);
        else
            logger.LogWarning("SILME BASARISIZ {ResourceType}/{AzId} (PusulaId={PusulaId}): HTTP {StatusCode}", source.ResourceType, source.AzResourceId, source.PusulaId, result.StatusCode);

        return entry;
    }

    private static SyncLogEntry CloneAsNew(SyncLogEntry source, SyncStatus status) => new()
    {
        ResourceType = source.ResourceType,
        PusulaId = source.PusulaId,
        Status = status,
        Operation = SyncOperation.Delete,
        PatientFullName = source.PatientFullName,
        FathersName = source.FathersName,
        BirthDate = source.BirthDate,
        Gender = source.Gender,
        Fin = source.Fin,
        RecordOpenedAt = source.RecordOpenedAt,
    };
}
