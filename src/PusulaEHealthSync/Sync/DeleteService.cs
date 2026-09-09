using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// Yanlislikla (veya test amacli) gonderilmis bir kaydi e-Health'ten geri almak icin --
// kullanicinin acikca istedigi bir guvenlik agi (bkz. konusma: "yanlislikla gonderilen
// verinin silinmesi var mi"). Sadece gercekten olusturulmus/guncellenmis (AzResourceId
// dolu) kayitlar silinebilir -- sadece $validate edilmis bir kaydin silinecek bir seyi yok.
public class DeleteService(EHealthClient eHealthClient, PusulaRepository repository, SyncLogStore syncLog, ILogger<DeleteService> logger)
{
    public async Task<SyncLogEntry> DeleteAsync(SyncLogEntry source, CancellationToken ct = default)
    {
        // Patoloji raporu TEK BASINA bir kayit degil, uc halkali bir zincirin tepesi
        // (DiagnosticReport -> Composition -> Observation, bkz. PathologyReportSyncService).
        // Sadece tepeyi silmek, bakanlikta yetim Composition ve Observation birakirdi --
        // kullanici "sildim" sanirken hasta verisi orada kalirdi. Bu yuzden zincirin tamami
        // silinir.
        if (source.ResourceType == "DiagnosticReport-Patoloji")
            return await DeletePathologyChainAsync(source, ct);

        return await DeleteOneAsync(source, ct);
    }

    // SIRA ONEMLI: her halka bir ALTTAKINE referans veriyor (DiagnosticReport -> Composition
    // -> Observation), FHIR ise hala referans edilen bir kaydi silmeyi HTTP 409 ile reddeder.
    // Bu yuzden REFERANS VEREN once gider: DiagnosticReport, sonra Composition, en son
    // Observation'lar. (Ayni kural Encounter/Procedure silmede de gecerli.)
    //
    // Tepe halka silinemezse alt halkalara HIC dokunulmaz -- yarim silinmis, tutarsiz bir
    // zincir birakmaktansa hicbir sey silmemek dogru davranis.
    private async Task<SyncLogEntry> DeletePathologyChainAsync(SyncLogEntry source, CancellationToken ct)
    {
        var reportEntry = await DeleteOneAsync(source, ct);
        if (reportEntry.Status != SyncStatus.Success)
            return reportEntry;

        // Composition -- PusulaId de rapor ile AYNI (ResultId), bkz. PathologyCompositionMapper.
        var compositions = await syncLog.GetLatestByPusulaIdsAsync("Composition-Patoloji", [source.PusulaId], ct);
        if (compositions.GetValueOrDefault(source.PusulaId) is { AzResourceId: not null } composition)
            await DeleteOneAsync(composition, ct);

        // Observation'lar -- PusulaId = EPulse.IslemReferansNumarasi, rapor ile dogrudan
        // baglantisi SyncLog'da tutulmuyor; bu yuzden hangi bulgularin gonderildigi Pusula'dan
        // yeniden hesaplaniyor. Kayit gonderildikten SONRA EPulse degistiyse (pratikte onayli
        // raporlarda beklenmez) bir bulgu gozden kacabilir -- o zaman Aktivite akisindan tek
        // tek silinebilir, artik "Patoloji Bulgusu" olarak dogru etiketle gorunuyorlar.
        var findings = await repository.GetPathologyFindingsByResultIdAsync(source.PusulaId, ct);
        if (findings.Count > 0)
        {
            var ids = findings.Select(f => f.IslemReferansNumarasi).ToList();
            var observations = await syncLog.GetLatestByPusulaIdsAsync("Observation-Patoloji", ids, ct);
            foreach (var id in ids)
                if (observations.GetValueOrDefault(id) is { AzResourceId: not null } observation)
                    await DeleteOneAsync(observation, ct);
        }

        return reportEntry;
    }

    private async Task<SyncLogEntry> DeleteOneAsync(SyncLogEntry source, CancellationToken ct = default)
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
