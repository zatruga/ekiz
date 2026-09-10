using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// SAATLIK OTOMATIK GONDERIM (Is 4). Ayarlar sayfasindaki otomatik gonderim alanlari
// (AutoSend.*) 2026-08'den beri duruyordu ama HICBIR SEY tarafindan okunmuyordu -- yani
// ekranda acilip kapanan bir anahtar vardi, arkasinda calisan bir sey yoktu. Bu servis
// o boslugu dolduruyor.
//
// VARSAYILAN KAPALI: AutoSend.Encounter.Enabled varsayilani false. Kullanici Ayarlar'dan
// acikca acmadan tek bir kayit bile gonderilmez. Bu bilincli -- dongu CANLI veri yaziyor
// ve kullanici canli dogrulamayi kendisi yapmayi tercih ediyor.
//
// HER TURDA:
//   1. Bekleyen isler yeniden hesaplanir (Bekleyen Isler sayfasi da bu sonucu gosterir)
//   2. Gonderime UYGUN protokoller, parti boyutu kadar, EncounterSyncService'e verilir
//   3. Iptal senkronu calisir (Pusula'da silinmis olanlar e-Health'ten de silinir)
//
// NEDEN PROTOKOL BAZLI GONDERIM: bekleyenleri kayit bazinda biliyoruz ama gonderimi
// protokol butununde yapiyoruz -- bagimlilik zinciri (Patient -> Encounter -> Procedure ->
// rapor) EncounterSyncService'te kodlu. Kayit bazli gonderim o zinciri yeniden yazmak
// olurdu; kazanci az, riski yuksek.
//
// KAPSAM (kullanici karari 2026-09-09): protokolun gonderime uygun olup olmadigi
// PendingWorkService.IsEligible'da belirlenir -- yatan hastada taburcu (90 gun tavanla),
// ayaktanda mevcut gun esigi kurali.
public class AutoSyncWorker(
    IServiceProvider services,
    ILogger<AutoSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Uygulama acilirken hemen dalmasin -- web tarafi ayaga kalksin, ayarlar okunabilsin.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = SettingsStore.AutoSendIntervalMinutesDefault;
            try
            {
                using var scope = services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<SettingsStore>();

                intervalMinutes = await settings.GetIntAsync(
                    SettingsStore.AutoSendIntervalMinutesKey, SettingsStore.AutoSendIntervalMinutesDefault, stoppingToken);
                var enabled = await settings.GetBoolAsync(
                    SettingsStore.AutoSendEncounterEnabledKey, false, stoppingToken);

                if (enabled)
                    await RunOnceAsync(scope.ServiceProvider, settings, stoppingToken);
                else
                    logger.LogDebug("Otomatik gonderim kapali -- tur atlandi.");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Tek bir turdaki hata donguyu OLDURMEMELI -- loglanip bir sonraki tura gecilir.
                logger.LogError(ex, "Otomatik gonderim turu hata ile sonlandi.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, intervalMinutes)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunOnceAsync(IServiceProvider sp, SettingsStore settings, CancellationToken ct)
    {
        var pendingWork = sp.GetRequiredService<PendingWorkService>();
        var encounterSync = sp.GetRequiredService<EncounterSyncService>();
        var cancellationSync = sp.GetRequiredService<CancellationSyncService>();

        var batchSize = await settings.GetIntAsync(
            SettingsStore.AutoSendBatchSizeKey, SettingsStore.AutoSendBatchSizeDefault, ct);

        // 1) Bekleyenleri hesapla. maxProtocols parti boyutundan BUYUK tutuluyor ki
        //    Bekleyen Isler sayfasi anlamli bir liste gosterebilsin.
        var pending = await pendingWork.RefreshAsync(
            PendingWorkService.DefaultScanDays, Math.Max(batchSize, 200), ct);

        var uygun = pending.Protokoller.Where(p => p.Eligible).Take(batchSize).ToList();
        logger.LogInformation(
            "Otomatik gonderim turu: {Toplam} protokolde bekleyen is var, bu turda {Bu} tanesi gonderiliyor (parti boyutu {Parti}).",
            pending.ToplamProtokolSayisi, uygun.Count, batchSize);

        int basarili = 0, basarisiz = 0;
        foreach (var p in uygun)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var sonuc = await encounterSync.SyncOneAsync(p.Protokol.ProtokolId, liveMode: true, ct);
                if (sonuc.Status == SyncStatus.Success) basarili++; else basarisiz++;
            }
            catch (Exception ex)
            {
                basarisiz++;
                logger.LogWarning(ex, "Otomatik gonderim: protokol {Id} gonderilemedi.", p.Protokol.ProtokolId);
            }
        }

        // 2) Iptal senkronu -- Pusula'da silinmis olanlari e-Health'ten de sil.
        var iptal = await cancellationSync.RunAsync(PendingWorkService.DefaultScanDays, ct);

        logger.LogInformation(
            "Otomatik gonderim turu bitti: {Basarili} basarili, {Basarisiz} basarisiz. " +
            "Iptal senkronu: {IptalProtokol} iptal protokol / {IptalIslem} iptal islem tarandi, {Silinen} kayit silindi, {Hata} hata.",
            basarili, basarisiz, iptal.IptalProtokol, iptal.IptalIslem, iptal.Silinen, iptal.Hata);
    }
}
