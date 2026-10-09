using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;

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

                // GUN SONU RAPORU -- ANAHTARIN DISINDA, BILEREK (2026-10-05).
                //
                // RunOnceAsync icine konulamazdi cunku o metot yalnizca otomatik gonderim
                // ACIKKEN cagriliyor. Rapor orada olsaydi, dongu kapandigi ya da coktugu
                // gun hic mail gelmezdi -- yani tam ihtiyac duyuldugu anda sessizlesirdi.
                //
                // Raporun isi "gonderim nasil gitti" degil, "GONDERIM OLDU MU" sorusuna
                // cevap vermek. Bu yuzden "hic gonderim yapilmadi" da raporlanabilir bir
                // sonuc (bkz. GunSonuRaporu.HicDenenmedi).
                await GunSonuRaporuDeneAsync(scope.ServiceProvider, settings, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Tek bir turdaki hata donguyu OLDURMEMELI -- loglanip bir sonraki tura gecilir.
                logger.LogError(ex, "Otomatik gonderim turu hata ile sonlandi.");

                // Hata da bir izdir: Ayarlar'daki durum kutusu "son tur hata verdi" diyebilsin.
                // Yazilmasaydi, surekli patlayan bir dongu ekranda hic calismamis gibi gorunurdu.
                try
                {
                    using var izScope = services.CreateScope();
                    var izSettings = izScope.ServiceProvider.GetRequiredService<SettingsStore>();
                    await izSettings.SetStringAsync(SettingsStore.AutoSendLastRunUtcKey, DateTime.UtcNow.ToString("O"), stoppingToken);
                    await izSettings.SetStringAsync(SettingsStore.AutoSendLastRunOzetKey, $"HATA: {ex.Message}", stoppingToken);
                }
                catch (Exception) { /* iz birakilamadiysa dongu yine de devam etmeli */ }
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
        // 2026-10-09: deger artik AYARDAN geliyor. Koda gomuluyken, bakanlik tarafinda
        // uzun suren bir kesinti yasandiginda esigi gecici olarak yukseltmenin yolu yoktu.
        var ustUsteIstisnaSiniri = Math.Max(1, await settings.GetIntAsync(
            SettingsStore.DevreKesiciSiniriKey, SettingsStore.DevreKesiciSiniriDefault, ct));
        int ustUsteIstisna = 0;

        foreach (var p in uygun)
        {
            if (ct.IsCancellationRequested) break;

            // "DURDUR" TUR ICINDE DE GECERLI (2026-10-05). Anahtar eskiden yalnizca tur
            // BASINDA okunuyordu: kullanici Ayarlar'dan Durdur'a bastiginda icinde bulunulan
            // tur sonuna kadar gonderime devam ediyordu. Buyuk protokollerle bir tur bir
            // saati bulabildigi icin bu, "durdurdum" denildikten sonra bir saat daha canli
            // veri yazmak demekti. Devlet kayit sistemine yazan bir dongude Durdur, DURDUR
            // anlamina gelmeli.
            //
            // Okuma ucuz: SettingsStore 3 sn'lik anlik goruntu onbellegi tutuyor, yani bu
            // kontrol protokol basina bir SQLite sorgusu degil.
            if (!await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct))
            {
                logger.LogWarning(
                    "Otomatik gonderim tur ortasinda DURDURULDU. {Gonderilen}/{Toplam} protokol gonderilmisti; "
                    + "kalanlar bir sonraki acilista yeniden siraya girer.",
                    basarili + basarisiz, uygun.Count);
                break;
            }

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

                if (ustUsteIstisna >= ustUsteIstisnaSiniri)
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
        //
        // DURDUR BURAYA DA GECIYOR (KULLANICI KARARI 2026-10-05: "durdur tum gonderim ve
        // silmeler icin gecerli olsun").
        //
        // Eskiden yalnizca ustteki gonderim dongusu kesiliyordu; tur ortasinda Durdur'a
        // basilsa bile bu satir calisiyor ve bakanlik sisteminden KALICI kayit siliyordu.
        // Gonderim idempotent -- yanlissa tekrar gonderilir; silme degil. Asimetri tam da
        // yanlis taraftaydi.
        //
        // Kapi HER SILMEDEN ONCE soruluyor (bkz. CancellationSyncService.RunAsync), yani
        // tur ortasinda durdurma da geciyor. Ayarlar okumasi ucuz: SettingsStore 3 sn'lik
        // anlik goruntu onbellegi tutuyor.
        var iptal = await cancellationSync.RunAsync(
            scanDays: null, ct,
            devamEdilsinMi: c => settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, c));

        // SON TURUN IZINI BIRAK. Ayarlar sayfasindaki durum kutusu bunu gosteriyor: "acik"
        // yazan bir anahtar dongunun gercekten calistigini kanitlamaz, son tur saati
        // kanitlar. Hata halinde de yaziliyor (asagidaki catch), yoksa olmus bir dongu
        // ekranda saglikli gorunurdu.
        var ozet = $"{basarili} basarili, {basarisiz} basarisiz · {uygun.Count} protokol denendi "
                 + $"· {iptal.Silinen} iptal kaydi silindi";
        await settings.SetStringAsync(SettingsStore.AutoSendLastRunUtcKey, DateTime.UtcNow.ToString("O"), ct);
        await settings.SetStringAsync(SettingsStore.AutoSendLastRunOzetKey, ozet, ct);
        logger.LogInformation(
            "Otomatik gonderim turu bitti: {Basarili} basarili, {Basarisiz} basarisiz. " +
            "Iptal senkronu: {IptalProtokol} protokol / {IptalIslem} islem / {IptalRadyoloji} radyoloji "
            + "tarandi, {Silinen} kayit silindi, {Hata} hata.",
            basarili, basarisiz,
            iptal.IptalProtokol, iptal.IptalIslem, iptal.IptalRadyoloji, iptal.Silinen, iptal.Hata);
    }


    // GUN SONU RAPORU (KULLANICI ISTEGI 2026-10-05): "gece o gun gonderdigi tum gonderimleri
    // kontrol edip gonderilmeyenleri gondermeye calisip, eger gonderilmez ise bir rapor
    // hazirlayip ilgili kisilere mail olarak iletelim."
    //
    // GONDERILMEYENLERI YENIDEN DENEME ISI BURADA DEGIL: onu zaten iki mekanizma yapiyor --
    // saatlik tur (durum bazli oldugu icin gonderilmeyen kalem listede kalir ve her turda
    // yeniden denenir) ve TakilanlariDeneAsync (gece, veri eksigi olanlar icin). Buraya
    // ucuncu bir deneme eklemek ayni kaydi ayni gece ucuncu kez gondermek olurdu.
    //
    // Eksik olan RAPORDU: bir sey kalici olarak gonderilemediginde kimse haberdar olmuyordu.
    // Ekrana bakmayi gerektiren bir bilgi, bakilmayan bir bilgidir.
    //
    // RAPORLANAN GUN DUNDUR (kullanici tarifi: "bu gun ayin 5, gelen mail 4 icin"): rapor
    // saati varsayilan 07:00 oldugu icin o an BUGUN henuz yarim; dun ise kapanmis bir gun.
    private async Task GunSonuRaporuDeneAsync(
        IServiceProvider sp, SettingsStore settings, CancellationToken ct)
    {
        try
        {
            // Mail kapaliysa raporu HESAPLAMIYORUZ bile -- hesap Pusula'ya protokol cozum
            // sorgulari atiyor, karsiliginda hicbir yere gitmeyecek bir rapor uretmek icin.
            if (!await settings.GetBoolAsync(SettingsStore.MailEnabledKey, false, ct)) return;

            var saat = await settings.GetIntAsync(
                SettingsStore.MailSendHourKey, SettingsStore.MailSendHourDefault, ct);
            var simdi = DateTime.Now;   // sunucu Baki saatinde

            // ">= saat", "== saat" DEGIL (2026-10-05 duzeltmesi). Esitlik kontrolu, o saatte
            // denk gelen tur gecikirse raporu O GUN TAMAMEN atliyordu: 06:50'de baslayip 70
            // dakika suren bir tur kontrole 08:00'de varir, "saat 7 degil" deyip gecer ve
            // ertesi sabaha kadar rapor gelmez. ">=" ile ilk firsatta gonderilir --
            // "bugun gonderildi mi" isareti zaten mukerrer gonderimi engelliyor.
            if (simdi.Hour < saat) return;

            // Ayni gun ikinci kez gitmesin (tur araligi 60 dk'dan kisaysa saat ayni kalir).
            var sonHam = await settings.GetStringAsync(SettingsStore.GunSonuLastRunKey, "", ct);
            if (DateOnly.TryParseExact(sonHam, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var son)
                && son >= DateOnly.FromDateTime(simdi))
                return;

            var raporServisi = sp.GetRequiredService<GunSonuRaporService>();
            var mail = sp.GetRequiredService<MailSender>();

            var gun = DateOnly.FromDateTime(simdi.AddDays(-1));
            var rapor = await raporServisi.OlusturAsync(gun, ct);

            var sonuc = await mail.GonderAsync(
                GunSonuMailHtml.Konu(rapor), GunSonuMailHtml.Govde(rapor), ct);

            // ISARET YALNIZCA BASARILI GONDERIMDEN SONRA ILERLER: SMTP gecici olarak
            // yanit vermediyse bir sonraki tur (bir saat sonra) yeniden dener. Isareti
            // kosulsuz yazsaydik, tek bir SMTP hatasi o gunun raporunu tamamen yakardi.
            if (sonuc.Basarili)
            {
                await settings.SetStringAsync(SettingsStore.GunSonuLastRunKey,
                    simdi.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ct);
                logger.LogInformation(
                    "Gun sonu raporu gonderildi ({Gun}): {Denenen} protokol denendi, {Basarili} basarili, "
                    + "{Hatali} hatali. {Mesaj}",
                    gun, rapor.DenenenProtokol, rapor.BasariliProtokol, rapor.HataliProtokol, sonuc.Mesaj);
            }
            else
            {
                logger.LogWarning(
                    "Gun sonu raporu ({Gun}) GONDERILEMEDI: {Mesaj} -- bir sonraki turda yeniden denenecek.",
                    gun, sonuc.Mesaj);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Rapor DONGUYU OLDURMEMELI: gonderim isi rapordan onemli.
            logger.LogError(ex, "Gun sonu raporu hazirlanirken hata olustu.");
        }
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

        // ">= saat", "== saat" DEGIL (2026-10-05 duzeltmesi). Saat kontrolu ana gonderim
        // partisi BITTIKTEN SONRA yapiliyor; 03:30'da baslayip 1,5 saat suren bir tur
        // buraya 05:00'te varir ve esitlik kontrolu "saat 4 degil" deyip o geceyi tamamen
        // atlardi. Yani tam da is yogun oldugu -- dolayisiyla takilanin cok oldugu --
        // gecelerde takilanlar hic denenmezdi. ">=" ile gecikse de ayni gece calisir.
        if (simdi.Hour < saat) return;

        // Ayni gun ikinci kez calismasin (tur araligi 60 dk'dan kisaysa saat ayni kalir).
        var sonMetin = await settings.GetStringAsync(SettingsStore.StuckRetryLastRunKey, "", ct);
        if (DateTime.TryParse(sonMetin, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var son)
            && son.Date == simdi.Date)
            return;

        var pendingWork = sp.GetRequiredService<PendingWorkService>();
        var protocolFullSync = sp.GetRequiredService<ProtocolFullSyncService>();
        // SINIR SAYI DEGIL SURE (KULLANICI SORUSU 2026-10-05: "100 denilen nedir, bence 10,
        // sinirlama yapmazsak ne olur?").
        //
        // Olculen gercek: takilan listesi 4.289 protokol ve her biri TAM ZINCIR demek
        // (hasta + muayine + tani + islem + epikriz + lab + rapor = onlarca HTTP cagrisi).
        //   Sinir yoksa : ~10 sn/protokol ile ~12 SAAT. Gece turu mesaiye tasar, saatlik
        //                 turlarla cakisir, ustelik bu protokollerin cogu kalici olarak
        //                 gonderilemez (LOINC kodu yok) -- yuk buyuk olcude bosa gider.
        //   10 olursa   : 4.289/10 = 429 gece, yani ~14 ay. Bugun verilen bir LOINC kodu
        //                 bir yil sonra denenir; rapor "duzeltin" dese bile anlamsiz olur.
        //
        // Dolayisiyla dogru sinir SAYI DEGIL SURE. Sure butcesi kendini ayarliyor: hizli
        // protokoller varsa cogu biter, yavassa azi -- ama tur hicbir zaman gune tasmaz.
        // 45 dk ile ~270 protokol/gece, tam tur ~16 gece.
        var sureDakika = await settings.GetIntAsync(
            SettingsStore.StuckRetryMaxMinutesKey, SettingsStore.StuckRetryMaxMinutesDefault, ct);
        var sonTarih = DateTime.UtcNow.AddMinutes(Math.Max(1, sureDakika));

        // TAM tarama: takilan kalemler tanimi geregi eskidir, artimli pencere onlari
        // gormezdi.
        // maxProtocols genis: artik sayiyla degil sureyle siniriyoruz, bu yuzden listenin
        // kirpilmasi butceyi fiilen daraltmasin. Kirpma yalnizca bellekte siralama/alma --
        // ag cagrisi yok, bedeli ihmal edilebilir.
        var sonuc = await pendingWork.RefreshAsync(maxProtocols: 2000, ct: ct);

        // SIRALAMA: EN UZUN SUREDIR DENENMEYEN once. Takilan listesi parti boyutundan buyuk
        // olabilir (olculdu: 30 gunde 4.289 protokol). Ana listedeki gibi "en eski kayit
        // once" deseydik, ayni ilk 100 protokol her gece yeniden denenir, geri kalani HIC
        // denenmezdi -- yani duzeltilmesi ana listeden cikarilan tuzagin aynisini bu listenin
        // icinde kurmus olurduk. Denemeden SONRA SyncLog zaman damgasi guncellendigi icin bu
        // siralama kendiliginden donuyor: her gece sira baskalarina geliyor.
        var hedefler = sonuc.TakilanProtokoller
            .Where(p => p.Eligible)
            .OrderBy(p => p.Items.Max(i => i.SonDeneme?.CreatedAtUtc ?? DateTime.MinValue))
            .ToList();

        logger.LogInformation(
            "Takilanlar turu basliyor: {Toplam} takilan protokol, sure butcesi {Dakika} dakika. "
            + "En uzun suredir denenmeyenden baslaniyor.",
            sonuc.ToplamTakilanProtokolSayisi, sureDakika);

        int duzelen = 0, denenen = 0;
        var durduruldu = false;
        foreach (var p in hedefler)
        {
            if (ct.IsCancellationRequested) { durduruldu = true; break; }

            // SURE BUTCESI DOLDU MU? Normal bitis -- kalanlar yarin gece, sira onlara gelir.
            if (DateTime.UtcNow >= sonTarih)
            {
                logger.LogInformation(
                    "Takilanlar turu sure butcesini doldurdu ({Dakika} dk): {Denenen}/{Toplam} protokol "
                    + "denendi, kalanlar bir sonraki gece denenecek.",
                    sureDakika, denenen, hedefler.Count);
                break;
            }

            // Gece turu da Durdur'a uymali -- ayni gerekce (bkz. RunOnceAsync).
            if (!await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct))
            {
                logger.LogWarning("Takilanlar turu ortasinda DURDURULDU ({Denenen} protokolden sonra).", denenen);
                durduruldu = true;
                break;
            }
            denenen++;
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

        // GUN ISARETI YALNIZCA DURDURULMADIYSA (2026-10-05 duzeltmesi). Eskiden kosulsuz
        // yaziliyordu: 04:00'te Durdur'a basip 04:30'da yeniden baslatan biri, o gece
        // takilanlarin hic denenmedigini fark etmezdi -- gun "yapildi" damgasini yemis
        // olurdu. Sure butcesinin dolmasi NORMAL bitistir, o damgalanir.
        if (!durduruldu)
            await settings.SetStringAsync(SettingsStore.StuckRetryLastRunKey, simdi.ToString("O"), ct);

        logger.LogInformation(
            "Takilanlar turu bitti: {Denenen} protokol denendi, {Duzelen} tanesi artik gonderildi. "
            + "{Durum}",
            denenen, duzelen,
            durduruldu ? "DURDURULDU -- gun isareti konmadi, ayni gece yeniden denenebilir." : "");
    }

}
