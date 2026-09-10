using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// IPTAL SENKRONU (Is 3) -- Pusula'da iptal edilmis olan ama e-Health'e GONDERILMIS
// kayitlari geri alir.
//
// DIGER SENKRONLARIN TERSI YONDE CALISIR: onlar "Pusula'da var, bizde yok" arar; bu
// "bizde var, Pusula'da artik gecersiz" arar. Bu yuzden ayri bir servis.
//
// KULLANICI KARARI (2026-09-09): silme OTOMATIK yapilir, onay adimi yok ("yanlislikla
// silme durumu olmayacaktir, silinmisse silinmis kabul edeceğiz"). Uyari kayda gecirildi:
// gonderim idempotenttir (yanlissa tekrar gonderilir) ama silme devlet kayit sisteminden
// KALICI veri cikarir.
//
// SILME SIRASI DISTAN ICERIYE: FHIR, hala referans edilen bir kaydi silmeyi HTTP 409 ile
// reddeder. Referans veren once gider:
//     DiagnosticReport (rapor) -> Composition -> Observation -> Procedure/Condition -> Encounter
// Patoloji zincirinin kendi ic sirasi DeleteService'te zaten kodlu (DiagnosticReport-Patoloji
// silinince Composition-Patoloji ve Observation-Patoloji de dogru sirada gider).
//
// SADECE GONDERDIKLERIMIZ: hic gonderilmemis bir kaydin iptali bizim icin olaysizdir --
// SyncLog'da Success + AzResourceId yoksa dokunulmaz.
public class CancellationSyncService(
    PusulaRepository repository,
    SyncLogStore syncLog,
    DeleteService deleteService,
    ILogger<CancellationSyncService> logger)
{
    public async Task<CancellationRunResult> RunAsync(
        int scanDays = PendingWorkService.DefaultScanDays, CancellationToken ct = default)
    {
        var fromLocal = DateTime.Now.Date.AddDays(-scanDays);
        int silinen = 0, hata = 0;

        // 1) IPTAL EDILMIS PROTOKOLLER -- butun zincir gecersiz.
        var iptalProtokoller = await repository.GetCancelledProtokollerAsync(fromLocal, ct);
        foreach (var p in iptalProtokoller)
        {
            var (ok, err) = await DeleteProtocolChainAsync(p.ProtokolId, ct);
            silinen += ok; hata += err;
        }
        var iptalProtokolIdSet = iptalProtokoller.Select(p => p.ProtokolId).ToHashSet();

        // 2) IPTAL EDILMIS TEK ISLEMLER -- protokolu ayakta ama islem iptal edilmis.
        //    Protokolu zaten iptal olanlar 1. adimda toptan halledildi, tekrar denenmez.
        var iptalIslemler = await repository.GetCancelledProceduresAsync(fromLocal, ct);
        foreach (var i in iptalIslemler)
        {
            if (iptalProtokolIdSet.Contains(i.ProtokolId)) continue;
            var (ok, err) = await DeleteProcedureWithDependentsAsync(i.ProtokolId, i.PusulaId, ct);
            silinen += ok; hata += err;
        }

        return new CancellationRunResult(iptalProtokoller.Count, iptalIslemler.Count, silinen, hata);
    }

    // Bir protokolun e-Health'teki HER kaydini distan iceriye siler. Cocuk kayitlarin
    // PusulaId'leri SyncLog'da protokole bagli tutulmadigi icin (her tip kendi id uzayini
    // kullanir) once Pusula'dan yeniden okunur.
    private async Task<(int Ok, int Err)> DeleteProtocolChainAsync(int protokolId, CancellationToken ct)
    {
        var hedefler = new List<(string ResourceType, int PusulaId)>();

        // -- En distaki halka: raporlar --
        foreach (var r in await repository.GetPathologyReportsByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("DiagnosticReport-Patoloji", r.ResultId));
        foreach (var r in await repository.GetRadiologyReportsByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("DiagnosticReport", r.TetkikIslemId));
        foreach (var l in await repository.GetLabResultsByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("Observation", l.LabaratuarSonucId));

        // -- Ortadaki halka: epikriz, islemler, tanilar --
        hedefler.Add(("Composition", protokolId));
        foreach (var i in await repository.GetIslemlerByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("Procedure", i.Id));
        foreach (var t in await repository.GetTanilarByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("Condition", t.Id));

        // -- En icteki halka: Muayine. EN SON silinir, cunku yukaridakilerin hepsi ona
        //    referans veriyor. (Patient BILEREK silinmez: baska protokollerde de kullaniliyor.)
        hedefler.Add(("Encounter", protokolId));

        return await DeleteTargetsAsync(hedefler, $"iptal protokol {protokolId}", ct);
    }

    // Tek bir islem iptal edildiginde: o isleme BAGLI raporlar once silinmeli, cunku
    // DiagnosticReport.extension:related-procedure ile Procedure'a referans veriyorlar --
    // once Procedure'i silmeye calisirsak HTTP 409 alir.
    private async Task<(int Ok, int Err)> DeleteProcedureWithDependentsAsync(int protokolId, int islemId, CancellationToken ct)
    {
        var hedefler = new List<(string, int)>();

        foreach (var r in await repository.GetPathologyReportsByProtokolIdAsync(protokolId, ct))
            if (r.ProtokolIslemId == islemId) hedefler.Add(("DiagnosticReport-Patoloji", r.ResultId));
        foreach (var r in await repository.GetRadiologyReportsByProtokolIdAsync(protokolId, ct))
            if (r.ProtokolIslemId == islemId) hedefler.Add(("DiagnosticReport", r.TetkikIslemId));

        hedefler.Add(("Procedure", islemId));

        return await DeleteTargetsAsync(hedefler, $"iptal işlem {islemId}", ct);
    }

    private async Task<(int Ok, int Err)> DeleteTargetsAsync(
        List<(string ResourceType, int PusulaId)> hedefler, string baglam, CancellationToken ct)
    {
        int ok = 0, err = 0;
        foreach (var (resourceType, pusulaId) in hedefler)
        {
            var son = await syncLog.GetLatestByPusulaIdsAsync(resourceType, [pusulaId], ct);
            if (son.GetValueOrDefault(pusulaId) is not { AzResourceId: not null } entry) continue;

            // Zaten silinmisse tekrar denenmez -- aksi halde her turda 404 uretirdi.
            if (entry.Operation == SyncOperation.Delete && entry.Status == SyncStatus.Success) continue;
            if (entry.Status != SyncStatus.Success) continue;

            var sonuc = await deleteService.DeleteAsync(entry, ct);
            if (sonuc.Status == SyncStatus.Success) ok++;
            else
            {
                err++;
                logger.LogWarning("Iptal senkronu silemedi ({Baglam}): {ResourceType}/{AzId} -- {Message}",
                    baglam, resourceType, entry.AzResourceId, sonuc.Message);
            }
        }
        return (ok, err);
    }
}

public record CancellationRunResult(int IptalProtokol, int IptalIslem, int Silinen, int Hata);
