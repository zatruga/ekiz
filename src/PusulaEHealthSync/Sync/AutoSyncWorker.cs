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
//   3. Iptal senkronu calisir (Pusula'da silinmis olanlar TRƏS'ten de silinir)
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
        var protocolFullSync = sp.GetRequiredService<ProtocolFullSyncService>();
        var cancellationSync = sp.GetRequiredService<CancellationSyncService>();

        var batchSize = await settings.GetIntAsync(
            SettingsStore.AutoSendBatchSizeKey, SettingsStore.AutoSendBatchSizeDefault, ct);

        // 1) Bekleyenleri hesapla. maxProtocols parti boyutundan BUYUK tutuluyor ki
        //    Bekleyen Isler sayfasi anlamli bir liste gosterebilsin.
        //
        // ARTIMLI TARAMA (2026-09-29): artik RefreshIncrementalAsync cagriliyor. Ayarlar'da
        // artimli mod KAPALIYSA bu metot kendiliginden eski tam taramaya duser, yani
        // davranis degismez. Acikken pencere "son isaretten beri"ye iner -- ayrintili
        // gerekce PendingWorkService.RefreshIncrementalAsync'te.
        var pending = await pendingWork.RefreshIncrementalAsync(Math.Max(batchSize, 200), ct);

        var uygun = pending.Protokoller.Where(p => p.Eligible).Take(batchSize).ToList();

        // HANGI ORTAMA YAZIYORUZ (KULLANICI ISTEGI 2026-10-03: "otomatik gönderimde sistem
        // sandbox seçili ise sandbox gönderim yapsın, ya da canlı seçili ise canlı").
        //
        // Davranis zaten dogruydu: EHealthClient.ResolveEndpointAsync ortami HER ISTEKTE
        // ayarlardan okuyor ve sabit kodlanmis adres yok, yani dongu hangi ortam seciliyse
        // oraya gidiyor. Eksik olan GORUNURLUKTU -- loga bakan kisi turun test mi canli mi
        // yazdigini anlayamiyordu. Canliya yazan bir dongude bu bilinmezlik kabul edilemez.
        var ortam = await settings.GetStringAsync(
            SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);
        logger.LogInformation(
            "Otomatik gonderim turu [{Ortam}]: {Toplam} protokolde bekleyen is var, bu turda {Bu} tanesi gonderiliyor "
            + "(parti boyutu {Parti}). Ayrica {Takilan} protokol takilanlar listesinde -- onlar bu turda denenmiyor.",
            ortam == "Live" ? "CANLI" : "TEST/SANDBOX",
            pending.ToplamProtokolSayisi, uygun.Count, batchSize, pending.ToplamTakilanProtokolSayisi);

        int basarili = 0, basarisiz = 0;

        // DEVRE KESICI (2026-09-28 inceleme). Bakanlik sunucusu ulasilamaz oldugunda eski
        // hal her protokol icin sirayla ag zaman asimina dusuyordu: 50 protokol x 100 sn
        // varsayilan HttpClient zaman asimi = bir turun 80 dakikayi asmasi, yani saatlik
        // aralikta turlarin birbirine yetismesi. Kimlik dogrulama da her protokolde
        // yeniden deneniyordu -- kapali bir sunucuya duzenli yuklenme.
        //
        // Ayrim bilincli: NORMAL basarisizlik (ornegin bakanligin reddettigi bir ICD-10
        // kodu) sayilmaz, cunku o protokole ozgudur ve digerleri gonderilebilir. Yalnizca
        // ISTISNA sayilir -- ag hatasi, kimlik dogrulama hatasi gibi altyapi sorunlari.
        // Ust uste bu kadar istisna geldiyse sorun tek tek protokollerde degil baglantida
        // demektir; tur birakilir, bir sonraki turda yeniden denenir.
        const int UstUsteIstisnaSiniri = 5;
        int ustUsteIstisna = 0;

        foreach (var p in uygun)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // Otomatik dongu de TAM zinciri gondermeli -- eskiden yalnizca Encounter
                // cascade'i cagriliyordu, yani dongu acilsaydi hicbir protokolun epikrizi,
                // laboratuvari ve patolojisi gitmeyecekti (2026-09-15'te toplu gonderimde
                // ayni eksik canli olarak yakalandi).
                var tam = await protocolFullSync.SyncAllAsync(p.Protokol, ct);
                if (tam.EncounterStatus == SyncStatus.Success) basarili++; else basarisiz++;
                ustUsteIstisna = 0;
            }
            catch (Exception ex)
            {
                basarisiz++;
                ustUsteIstisna++;
                logger.LogWarning(ex, "Otomatik gonderim: protokol {Id} gonderilemedi.", p.Protokol.ProtokolId);

                if (ustUsteIstisna >= UstUsteIstisnaSiniri)
                {
                    logger.LogError(
                        "Otomatik gonderim: ust uste {Sayi} protokolde istisna olustu -- baglanti/kimlik "
                        + "sorunu varsayiliyor, bu tur birakiliyor. Kalan {Kalan} protokol bir sonraki turda denenecek.",
                        ustUsteIstisna, uygun.Count - (basarili + basarisiz));
                    break;
                }
            }
        }

        // 2) TAKILANLAR -- gunde bir kez, kendi turunda (bkz. TakilanlariDeneAsync).
        await TakilanlariDeneAsync(sp, settings, ct);

        // 3) Iptal senkronu -- Pusula'da silinmis olanlari TRƏS'ten de sil.
        var iptal = await cancellationSync.RunAsync(scanDays: null, ct);

        logger.LogInformation(
            "Otomatik gonderim turu bitti: {Basarili} basarili, {Basarisiz} basarisiz. " +
            "Iptal senkronu: {IptalProtokol} protokol / {IptalIslem} islem / {IptalRadyoloji} radyoloji "
            + "tarandi, {Silinen} kayit silindi, {Hata} hata.",
            basarili, basarisiz,
            iptal.IptalProtokol, iptal.IptalIslem, iptal.IptalRadyoloji, iptal.Silinen, iptal.Hata);
    }

    // TAKILANLARIN GUNDE BIR KEZLIK DENEMESI (KULLANICI KARARI 2026-10-03: "onlar için
    // ayrıca deneme yapsın").
    //
    // Takilan kalem = mevcut Pusula verisiyle gonderilemeyecek olan (LOINC kodu yok, sonuc
    // degeri bos, Icbari eslesmesi yok) ya da ust uste basarisiz olmus kalem. Saatlik tur
    // bunlara HIC dokunmuyor -- dokunsaydi, liste en eskiden basladigi icin parti
    // kontenjaninin tamamini kalici olarak isgal ederlerdi.
    //
    // Yine de buraya bir deneme gerekiyor: eksik giderilebilir. Birisi LOINC Eslestirme
    // sayfasindan kod verdiginde ya da laboratuvar sonucu girdiginde kaydin kendiliginden
    // gitmesi lazim; aksi halde kullanicinin her birini elle bulup gondermesi gerekirdi.
    //
    // GUNDE BIR KEZ ve GECE: hem bakanlik sunucusuna gereksiz yuk binmesin hem de gunduz
    // saatlerindeki asil gonderimin kontenjanini yemesin.
    private async Task TakilanlariDeneAsync(IServiceProvider sp, SettingsStore settings, CancellationToken ct)
    {
        var saat = await settings.GetIntAsync(
            SettingsStore.StuckRetryHourKey, SettingsStore.StuckRetryHourDefault, ct);
        var simdi = DateTime.Now;   // sunucu zaten Baki saatinde
        if (simdi.Hour != saat) return;

        // Ayni gun ikinci kez calismasin (tur araligi 60 dk'dan kisaysa saat ayni kalir).
        var sonMetin = await settings.GetStringAsync(SettingsStore.StuckRetryLastRunKey, "", ct);
        if (DateTime.TryParse(sonMetin, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var son)
            && son.Date == simdi.Date)
            return;

        var pendingWork = sp.GetRequiredService<PendingWorkService>();
        var protocolFullSync = sp.GetRequiredService<ProtocolFullSyncService>();
        var parti = await settings.GetIntAsync(
            SettingsStore.StuckRetryBatchSizeKey, SettingsStore.StuckRetryBatchSizeDefault, ct);

        // TAM tarama: takilan kalemler tanimi geregi eskidir, artimli pencere onlari
        // gormezdi.
        var sonuc = await pendingWork.RefreshAsync(maxProtocols: Math.Max(parti, 200), ct: ct);

        // SIRALAMA: EN UZUN SUREDIR DENENMEYEN once. Takilan listesi parti boyutundan buyuk
        // olabilir (olculdu: 30 gunde 4.289 protokol). Ana listedeki gibi "en eski kayit
        // once" deseydik, ayni ilk 100 protokol her gece yeniden denenir, geri kalani HIC
        // denenmezdi -- yani duzeltilmesi ana listeden cikarilan tuzagin aynisini bu listenin
        // icinde kurmus olurduk. Denemeden SONRA SyncLog zaman damgasi guncellendigi icin bu
        // siralama kendiliginden donuyor: her gece sira baskalarina geliyor.
        var hedefler = sonuc.TakilanProtokoller
            .Where(p => p.Eligible)
            .OrderBy(p => p.Items.Max(i => i.SonDeneme?.CreatedAtUtc ?? DateTime.MinValue))
            .Take(parti)
            .ToList();

        logger.LogInformation(
            "Takilanlar turu: {Toplam} takilan protokolden {Bu} tanesi yeniden deneniyor.",
            sonuc.ToplamTakilanProtokolSayisi, hedefler.Count);

        int duzelen = 0;
        foreach (var p in hedefler)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var tam = await protocolFullSync.SyncAllAsync(p.Protokol, ct);
                if (tam.EncounterStatus == SyncStatus.Success) duzelen++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Takilanlar turu: protokol {Id} yine gonderilemedi.", p.Protokol.ProtokolId);
            }
        }

        await settings.SetStringAsync(SettingsStore.StuckRetryLastRunKey, simdi.ToString("O"), ct);
        logger.LogInformation("Takilanlar turu bitti: {Duzelen}/{Deneme} protokol gonderildi.",
            duzelen, hedefler.Count);
    }

}
