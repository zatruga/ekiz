using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync;

// v1: tek gecisli (bir kere calisip duran), guvenli varsayilanli senkron denemesi.
// - Varsayilan: sadece $validate cagirir (SEND_LIVE=true olmadan hicbir POST/PUT atmaz).
// - Varsayilan: sadece SYNC_COUNT kadar (varsayilan 1) kayit isler, tum tabloyu degil.
// - RESOURCE_TYPE=Encounter + TARGET_PROTOKOL_ID ile Encounter da manuel tetiklenebilir
//   (surekli/otomatik Encounter dongusu henuz yok -- once kapanmamis protokol karari lazim).
// Bu sinirlar bilerek konuldu: gercek hasta verisiyle calisirken once kucuk, geri
// donusu kolay adimlarla ilerlemek icin (bkz. konusma gecmisindeki MVP karari).
// Asil map/gonder/logla mantigi *SyncService siniflarinda -- web dashboard'daki "gonder"
// butonlari da ayni servisleri kullaniyor.
public class Worker(
    ILogger<Worker> logger,
    PusulaRepository repository,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var liveMode = Environment.GetEnvironmentVariable("SEND_LIVE") == "true";
        var resourceType = Environment.GetEnvironmentVariable("RESOURCE_TYPE") ?? "Patient";

        if (resourceType == "Encounter")
        {
            var targetProtokolId = int.Parse(Environment.GetEnvironmentVariable("TARGET_PROTOKOL_ID")
                ?? throw new InvalidOperationException("RESOURCE_TYPE=Encounter icin TARGET_PROTOKOL_ID zorunlu"));
            logger.LogInformation("Baslatiliyor. Kaynak: Encounter, Mod: {Mode}, Protokol Id: {Id}",
                liveMode ? "LIVE (POST/PUT atilacak)" : "VALIDATE-ONLY", targetProtokolId);
            var encResult = await encounterSyncService.SyncOneAsync(targetProtokolId, liveMode, stoppingToken);
            logger.LogInformation("Bitti. Durum={Status} Mesaj={Message}", encResult.Status, encResult.Message ?? "(yok)");
            lifetime.StopApplication();
            return;
        }

        var syncCount = int.TryParse(Environment.GetEnvironmentVariable("SYNC_COUNT"), out var n) ? n : 1;
        var targetHastaId = int.TryParse(Environment.GetEnvironmentVariable("TARGET_HASTA_ID"), out var t) ? t : (int?)null;

        logger.LogInformation("Baslatiliyor. Kaynak: Patient, Mod: {Mode}, Kayit sayisi: {Count}, Hedef Id: {Target}",
            liveMode ? "LIVE (POST/PUT atilacak)" : "VALIDATE-ONLY (sadece $validate, kayit atilmayacak)",
            syncCount, targetHastaId?.ToString() ?? "(yok)");

        List<int> hastaIdler;
        if (targetHastaId is not null)
        {
            hastaIdler = [targetHastaId.Value];
        }
        else
        {
            var hastalar = await repository.GetRecentHastalarAsync(syncCount, stoppingToken);
            hastaIdler = hastalar.Select(h => h.Id).ToList();
        }
        logger.LogInformation("Islenecek hasta sayisi: {Count}", hastaIdler.Count);

        int ok = 0, skipped = 0, failed = 0;
        foreach (var hastaId in hastaIdler)
        {
            var result = await patientSyncService.SyncOneAsync(hastaId, liveMode, stoppingToken);
            switch (result.Status)
            {
                case SyncStatus.Success: ok++; break;
                case SyncStatus.Skipped: skipped++; break;
                case SyncStatus.Failed: failed++; break;
            }
        }

        logger.LogInformation("Bitti. Basarili={Ok} Atlanan={Skipped} Hatali={Failed}", ok, skipped, failed);
        lifetime.StopApplication();
    }
}
