using System.Globalization;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// "Bekleyen Isler" -- Pusula'da HAZIR olup TRƏS'e HENUZ GITMEMIS her sey.
//
// TASARIMIN OZU (2026-09-09, kullanici ile birlikte kararlastirildi):
//
//   Ne olmaliydi  = Pusula (onayli lab/radyoloji/patoloji, girilmis islem, kilitli epikriz)
//   Ne gonderdik  = SyncLog (ResourceType + PusulaId ikilisiyle)
//   BEKLEYEN      = birincisi eksi ikincisi
//
// KRITIK: kriter "GONDERILMEMIS OLMAK" (durum), "bugun sonuclanmis olmak" (olay) DEGIL.
// Olay bazli kurulsaydi dongu bir gun calismadiginda o gunun kayitlari kalici olarak
// kaybolurdu. Durum bazli oldugu icin sistem kendini onarir: kacan kayit gonderilene kadar
// listede kalir. Tarih SADECE taramayi sinirlar (performans), karar kriteri degildir.
//
// TAKIP KAYIT BAZLI, YURUTME PROTOKOL BAZLI: hangi kalemin eksik oldugunu kayit kayit
// biliyoruz, ama gonderim EncounterSyncService uzerinden protokol butununde yapiliyor --
// cunku bagimlilik zinciri (Patient -> Encounter -> Procedure -> rapor) orada kodlu.
public class PendingWorkService(
    PusulaRepository repository,
    SyncLogStore syncLog,
    SettingsStore settings)
{
    // Patoloji immunohistokimya ile haftalar surebiliyor; pencere bunu rahat kapsamali.
    // ARTIK SADECE VARSAYILAN: gercek pencere Ayarlar'dan geliyor (bkz. TaramaBaslangiciAsync).
    public const int DefaultScanDays = 60;

    // Taramanin baslangic tarihini Ayarlar'dan cozer (KULLANICI ISTEGI 2026-09-28).
    //
    // Iki mod var ve ikisi de ayni seye cevap veriyor -- "nereden itibaren tara":
    //   Preset : bugunden N gun geriye  (son 1 ay, son 3 ay, ...)
    //   Date   : girilen tarihten itibaren (sabit bir baslangic noktasi)
    //
    // Statik ve SettingsStore aliyor cunku ayni pencereyi iptal senkronu da kullaniyor
    // (CancellationSyncService) -- iki yerde ayri ayri cozulurse birbirinden kayarlar.
    public static async Task<DateTime> TaramaBaslangiciAsync(SettingsStore settings, CancellationToken ct)
    {
        var mod = await settings.GetStringAsync(
            SettingsStore.PendingScanModeKey, SettingsStore.PendingScanModeDefault, ct);

        if (string.Equals(mod, "Date", StringComparison.OrdinalIgnoreCase))
        {
            var ham = await settings.GetStringAsync(SettingsStore.PendingScanFromDateKey, "", ct);
            if (DateOnly.TryParseExact(ham, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var tarih))
                return tarih.ToDateTime(TimeOnly.MinValue);
            // Tarih modu secili ama tarih bos/bozuksa: sessizce TUM gecmisi taramak yerine
            // guvenli varsayilana dus. Sinirsiz tarama tam da kacinmak istedigimiz sey.
        }

        var gun = await settings.GetIntAsync(
            SettingsStore.PendingScanPresetDaysKey, SettingsStore.PendingScanPresetDaysDefault, ct);
        return DateTime.Now.Date.AddDays(-Math.Max(1, gun));
    }

    // Yatan hastada kural "taburcu olunca gonder". Ama protokol veri girisi hatasiyla hic
    // kapanmazsa sonsuza dek beklerdi -- kullanici karari (2026-09-09): makul bir tavan koy.
    public const int YatanMaxOpenDays = 90;

    // ONBELLEK: tam tarama canli veride ~15 sn suruyor (7 gunluk pencerede 54.000
    // laboratuvar + 24.000 islem adayi taraniyor) -- her sayfa acilisinda calistirilamaz.
    // Sonuc burada tutulur; Bekleyen Isler sayfasi bunu gosterir, saatlik dongu her
    // turunda tazeler, kullanici da "Yenile" ile zorlayabilir.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    public PendingWorkResult? SonSonuc { get; private set; }
    public DateTime? SonHesaplamaUtc { get; private set; }

    // ARTIMLI TARAMA -- yalnizca otomatik gonderim dongusu kullanir (AutoSyncWorker).
    //
    // Pencere IKI SINIR arasina sikistirilir:
    //   UST SINIR : Ayarlar'daki pencere. Tarama bundan daha GENIS olamaz, yani en kotu
    //               durumda bugunku davranisin aynisi olur -- hicbir sey kotulesemez.
    //   ALT SINIR : son basarili taramanin isareti (guvenlik payi kadar geriden).
    //
    // Aradaki belirleyici: HALA BEKLEYEN en eski kalem. Pencere onun gerisine asla
    // cekilmez. Sonuc:
    //   - her sey akiyorsa  -> pencere ~1 saat, sorgu kucucuk
    //   - bir kalem takildiysa -> pencere kendiliginden o tarihe kadar genisler
    //   - kalem kalici olarak gonderilemiyorsa (orn. bakanligin reddettigi ICD-10) ->
    //     ust sinir devreye girer, pencere sonsuza kadar acilmaz
    //
    // Gunde bir kez TAM SUPURME yapilir: geriye donuk duzeltmeleri (biri dunun onay
    // tarihini elle degistirirse) artimli tarama kaciririr, supurme yakalar.
    public async Task<PendingWorkResult> RefreshIncrementalAsync(
        int maxProtocols = 200, CancellationToken ct = default)
    {
        var acik = await settings.GetBoolAsync(SettingsStore.PendingScanIncrementalEnabledKey, false, ct);
        var tamPencere = await TaramaBaslangiciAsync(settings, ct);

        if (!acik)
            return await RefreshAsync(scanDays: null, maxProtocols, ct);

        var simdiUtc = DateTime.UtcNow;
        var supurmeGerekli = await TamSupurmeGerekliMiAsync(ct);
        var baslangic = supurmeGerekli
            ? tamPencere
            : await ArtimliBaslangicAsync(tamPencere, ct);

        await _refreshLock.WaitAsync(ct);
        try
        {
            var sonuc = await HesaplaAsync(baslangic, null, maxProtocols, ct);
            SonSonuc = sonuc;
            SonHesaplamaUtc = DateTime.UtcNow;
            SonTaramaBaslangici = baslangic;
            SonTaramaBitisi = null;

            // ISARETLER YALNIZCA BASARILI TARAMADAN SONRA ILERLER. Tarama ortasinda
            // istisna olursa buraya hic gelinmez, yani bir sonraki tur ayni yerden
            // devam eder -- kayit atlanmaz.
            await settings.SetStringAsync(SettingsStore.PendingScanWatermarkKey,
                simdiUtc.ToString("O", CultureInfo.InvariantCulture), ct);
            await EnEskiBekleyeniKaydetAsync(sonuc, ct);
            if (supurmeGerekli)
                await settings.SetStringAsync(SettingsStore.PendingScanLastFullSweepKey,
                    DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);

            return sonuc;
        }
        finally { _refreshLock.Release(); }
    }

    // Gunde bir kez, yapilandirilan saatte. Hic yapilmadiysa ILK turda yapilir --
    // boylece artimli mod acilir acilmaz once tam bir resim cikarilmis olur.
    private async Task<bool> TamSupurmeGerekliMiAsync(CancellationToken ct)
    {
        var sonHam = await settings.GetStringAsync(SettingsStore.PendingScanLastFullSweepKey, "", ct);
        if (!DateOnly.TryParseExact(sonHam, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var son))
            return true;

        if (son >= DateOnly.FromDateTime(DateTime.Now)) return false;

        var saat = await settings.GetIntAsync(
            SettingsStore.PendingScanFullSweepHourKey, SettingsStore.PendingScanFullSweepHourDefault, ct);
        return DateTime.Now.Hour >= saat;
    }

    private async Task<DateTime> ArtimliBaslangicAsync(DateTime tamPencere, CancellationToken ct)
    {
        var isaretHam = await settings.GetStringAsync(SettingsStore.PendingScanWatermarkKey, "", ct);
        DateTime? isaretUtc = DateTime.TryParse(isaretHam, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var i) ? i : null;

        var enEskiHam = await settings.GetStringAsync(SettingsStore.PendingScanOldestPendingKey, "", ct);
        DateTime? enEski = DateTime.TryParse(enEskiHam, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var e) ? e : null;

        return ArtimliPencere(tamPencere, isaretUtc, enEski);
    }

    // Pencere hesabinin TAMAMI -- saf fonksiyon, yan etkisiz, dogrudan test edilebilir.
    // Artimli taramanin butun guvenligi bu uc satirda toplandigi icin ayri duruyor.
    //
    //   tamPencere : Ayarlar'daki baslangic (UST SINIR -- daha geriye gidilmez)
    //   isaretUtc  : son basarili taramanin basladigi an; null ise hic tarama yapilmamis
    //   enEskiBekleyen : hala gonderilememis en eski kalemin tarihi (yerel); null ise yok
    public static DateTime ArtimliPencere(DateTime tamPencere, DateTime? isaretUtc, DateTime? enEskiBekleyen)
    {
        // Hic isaret yoksa (ilk calisma) tam pencere taranir -- eksik resimle baslamayalim.
        if (isaretUtc is not { } isaret) return tamPencere;

        // Isaret UTC tutuluyor, Pusula tarihleri YEREL -- karsilastirmadan once cevir.
        // Guvenlik payi: isaretin biraz GERISINDEN basla (saat kaymasi, gec commit).
        var baslangic = AzTime.ToLocal(isaret).AddMinutes(-SettingsStore.PendingScanOverlapMinutes);

        // Hala bekleyen en eski kalemin gerisine cekilme -- yoksa takilan kayit listeden
        // duser ve bir daha hic gonderilmez. Kendini onarma ozelligi TAM BURADA korunuyor.
        if (enEskiBekleyen is { } eski && eski < baslangic) baslangic = eski;

        // UST SINIR: Ayarlar'daki pencereden daha geriye asla gidilmez. Bu olmasaydi,
        // kalici olarak gonderilemeyen tek bir kalem (orn. bakanligin reddettigi bir
        // ICD-10 kodu) pencereyi sonsuza kadar acik tutardi.
        return baslangic < tamPencere ? tamPencere : baslangic;
    }

    private async Task EnEskiBekleyeniKaydetAsync(PendingWorkResult sonuc, CancellationToken ct)
    {
        // DIKKAT: ozet TUM bekleyenleri kapsar ama Protokoller listesi maxProtocols ile
        // KIRPILMIS olabilir. Kirpilmis listeden hesaplanan "en eski" yaniltici olurdu,
        // bu yuzden kirpilmamis en eski tarih ayrica tasiniyor (bkz. PendingWorkResult).
        if (sonuc.EnEskiBekleyenTarih is { } t)
            await settings.SetStringAsync(SettingsStore.PendingScanOldestPendingKey,
                t.ToString("O", CultureInfo.InvariantCulture), ct);
        else
            await settings.SetStringAsync(SettingsStore.PendingScanOldestPendingKey, "", ct);
    }

    // Son turun fiilen kullandigi aralik -- ekranda gostermek icin.
    public DateTime? SonTaramaBaslangici { get; private set; }
    public DateTime? SonTaramaBitisi { get; private set; }

    // scanDays null ise pencere Ayarlar'dan okunur. Sayi verilirse (Bekleyen Isler
    // sayfasindaki elle "gun" kutusu) o tur icin ayar GECICI olarak ezilir.
    // toLocalExclusive: bitis siniri (haric). null = sinir yok -- otomatik dongu boyle
    // cagiriyor, yani davranisi degismiyor. Bekleyen Isler sayfasi iki tarih secimiyle
    // acikca aralik verebiliyor (KULLANICI ISTEGI 2026-09-29).
    public async Task<PendingWorkResult> RefreshAsync(
        int? scanDays = null, int maxProtocols = 200, CancellationToken ct = default,
        DateTime? fromLocalOverride = null, DateTime? toLocalExclusive = null)
    {
        // Ayni anda iki tarama baslamasin (sayfa + dongu ayni saniyede tetiklerse).
        await _refreshLock.WaitAsync(ct);
        try
        {
            var baslangic = fromLocalOverride
                ?? (scanDays is { } gun && gun > 0
                    ? DateTime.Now.Date.AddDays(-gun)
                    : await TaramaBaslangiciAsync(settings, ct));
            var sonuc = await HesaplaAsync(baslangic, toLocalExclusive, maxProtocols, ct);
            SonSonuc = sonuc;
            SonHesaplamaUtc = DateTime.UtcNow;
            SonTaramaBaslangici = baslangic;
            SonTaramaBitisi = toLocalExclusive;
            return sonuc;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task<PendingWorkResult> GetPendingAsync(
        int? scanDays = null, int maxProtocols = 200, CancellationToken ct = default)
    {
        var baslangic = scanDays is { } gun && gun > 0
            ? DateTime.Now.Date.AddDays(-gun)
            : await TaramaBaslangiciAsync(settings, ct);
        return await HesaplaAsync(baslangic, null, maxProtocols, ct);
    }

    // Asil hesap. Baslangic tarihi DISARIDAN verilir -- artimli tarama da, tam tarama da,
    // ekrandaki elle "gun" kutusu da ayni koddan gecer; yalnizca baslangic degisir.
    private async Task<PendingWorkResult> HesaplaAsync(
        DateTime fromLocal, DateTime? toLocalExclusive, int maxProtocols, CancellationToken ct)
    {

        // 1) Pusula'da hazir olan her sey. Her kaynak KENDI sonuclanma tarihiyle taranir.
        var candidates = new List<PendingCandidate>();
        candidates.AddRange(await repository.GetCompletedLabResultsAsync(fromLocal, toLocalExclusive, ct));
        candidates.AddRange(await repository.GetCompletedRadiologyAsync(fromLocal, toLocalExclusive, ct));
        candidates.AddRange(await repository.GetCompletedPathologyAsync(fromLocal, toLocalExclusive, ct));
        candidates.AddRange(await repository.GetCreatedProceduresAsync(fromLocal, toLocalExclusive, ct));
        candidates.AddRange(await repository.GetLockedEpikrizAsync(fromLocal, toLocalExclusive, ct));

        // 2) Protokolun KENDISI de bir kalem: Muayine (Encounter) gonderilmemisse altindaki
        //    hicbir sey gonderilemez (hepsi azEncounterId'ye bagli). Bu yuzden aday listesine
        //    her protokol icin bir Encounter kalemi ekleniyor.
        // ToList() SART: asagida candidates'a ekleme yapiliyor, tembel Distinct() dogrudan
        // uzerinde donseydi "Collection was modified" hatasi alirdi.
        foreach (var protokolId in candidates.Select(c => c.ProtokolId).Distinct().ToList())
            candidates.Add(new PendingCandidate
            {
                ProtokolId = protokolId,
                PusulaId = protokolId,
                ResourceType = "Encounter",
                Baslik = "Müayinə",
                SonuclanmaTarihi = DateTime.MinValue,
            });

        if (candidates.Count == 0) return PendingWorkResult.Bos;

        // 3) SyncLog'da ne var? Kaynak tipi bazinda toplu okuma.
        var sentLookup = new Dictionary<(string, int), SyncLogEntry>();
        foreach (var group in candidates.GroupBy(c => c.ResourceType))
        {
            var ids = group.Select(c => c.PusulaId).Distinct().ToList();
            // govdeleriGetir: false -- burada yalnizca DURUM'a bakiliyor, gonderilen FHIR
            // govdesi hic kullanilmiyor. Tarama 54.000'i askin id ile calistigi icin bu iki
            // kolonu okumamak ciddi bellek/G-C tasarrufu (bkz. SyncLogStore).
            var sent = await syncLog.GetLatestByPusulaIdsAsync(group.Key, ids, ct, govdeleriGetir: false);
            foreach (var kv in sent) sentLookup[(group.Key, kv.Key)] = kv.Value;
        }

        // 3b) Ust uste kac kez basarisiz olmuslar? SADECE son durumu basarisiz olanlar icin --
        //     iliskili alt sorguyu 54.000 id'nin hepsi icin calistirmak gereksiz, basarisiz
        //     alt kume ise kucuk (bkz. SyncLogStore.GetArdisikBasarisizSayilariAsync).
        var ardisikBasarisiz = new Dictionary<(string, int), int>();
        foreach (var group in sentLookup
                     .Where(kv => kv.Value.Status is not SyncStatus.Success and not SyncStatus.Skipped)
                     .GroupBy(kv => kv.Key.Item1))
        {
            var ids = group.Select(kv => kv.Key.Item2).Distinct().ToList();
            var sayilar = await syncLog.GetArdisikBasarisizSayilariAsync(group.Key, ids, ct);
            foreach (var kv in sayilar) ardisikBasarisiz[(group.Key, kv.Key)] = kv.Value;
        }

        // 4) IKI AYRI KUME. KULLANICI KARARI (2026-10-03): "bekleyenleri ayrı bir listeye
        //    alalım ... bizim ana gönderim listesine sayıları hiç karışmasın."
        // Ayarlar'daki "En fazla kac kez dene?" -- 2026-10-05'e kadar okunmuyordu.
        var takilmaEsigi = Math.Max(1, await settings.GetIntAsync(
            SettingsStore.RetryMaxAttemptsKey, TakilmaEsigiVarsayilan, ct));

        var bekleyen = new List<PendingCandidate>();
        var takilan = new List<PendingCandidate>();
        var gonderilmisKalem = 0;

        // Protokol basina IKI bayrak: eksigi var mi, gideni var mi. Ikisi birlikte uc
        // dilimi veriyor (hic gonderilmemis / kismen / tamamlanmis).
        var eksigiOlan = new HashSet<int>();
        var gidenOlan = new HashSet<int>();

        foreach (var c in candidates)
        {
            switch (Siniflandir(c, sentLookup, ardisikBasarisiz, takilmaEsigi))
            {
                case KalemDurumu.Bekliyor:
                    bekleyen.Add(c); eksigiOlan.Add(c.ProtokolId); break;
                case KalemDurumu.Takildi:
                    takilan.Add(c); eksigiOlan.Add(c.ProtokolId); break;
                default:
                    gonderilmisKalem++; gidenOlan.Add(c.ProtokolId); break;
            }
        }

        var tarananProtokol = candidates.Select(c => c.ProtokolId).Distinct().Count();
        var kismen = eksigiOlan.Count(id => gidenOlan.Contains(id));
        var kapsam = new PendingKapsam(
            TarananProtokol: tarananProtokol,
            TamamlananProtokol: tarananProtokol - eksigiOlan.Count,
            KismenGonderilmisProtokol: kismen,
            HicGonderilmemisProtokol: eksigiOlan.Count - kismen,
            TarananKalem: candidates.Count,
            GonderilmisKalem: gonderilmisKalem,
            BekleyenKalem: bekleyen.Count,
            TakilanKalem: takilan.Count);

        // Her sey gonderilmisse de KAPSAM donuyor -- "bekleyen is yok" ile "taranacak bir
        // sey yoktu" ayni sey degil, ekranda ikisi ayirt edilebilmeli.
        if (bekleyen.Count == 0 && takilan.Count == 0)
            return PendingWorkResult.Bos with { Kapsam = kapsam };

        // 5) Protokol bilgisi + uygunluk kurallari. Iki kume de ayni protokol tablosundan
        //    besleniyor, bu yuzden tek okuma.
        var protokoller = await repository.GetProtokollerByIdsAsync(
            bekleyen.Concat(takilan).Select(p => p.ProtokolId).Distinct().ToList(), ct);
        var openAfterDays = await settings.GetIntAsync(
            SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);
        var minYas = await settings.GetIntAsync(
            SettingsStore.MinProtokolYasiGunKey, SettingsStore.MinProtokolYasiGunDefault, ct);

        // SOGUMA SURESI -- Ayarlar'daki "Kac dakika sonra tekrar dene?" (2026-10-05'te
        // gercekten baglandi; o alan da okunmuyordu).
        //
        // NEDEN PROTOKOL SEVIYESINDE, KALEM SEVIYESINDE DEGIL: gonderim birimi protokol.
        // Tek bir kalemi "sogumada" diye atlayip protokolu yine gondermek hicbir sey
        // kazandirmaz -- zincir zaten bastan calisir. Kural o yuzden sudur: protokolun
        // BEKLEYEN kalemlerinin TAMAMI yakin zamanda denenip basarisiz olmussa, protokol
        // bu tur atlanir. Hic denenmemis tek bir kalem varsa protokol gider -- cunku o
        // kalem icin beklemenin anlami yok.
        var sogumaDakika = Math.Max(0, await settings.GetIntAsync(
            SettingsStore.RetryIntervalMinutesKey, SettingsStore.RetryIntervalMinutesDefault, ct));
        var simdiUtc = DateTime.UtcNow;

        // GONDERIM TABAN TARIHI (kullanici karari 2026-10-05). Bos ise taban yok.
        var tabanHam = await settings.GetStringAsync(SettingsStore.SendFloorDateKey, "", ct);
        DateOnly? tabanTarih = DateOnly.TryParseExact(tabanHam, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

        List<PendingProtocol> Grupla(List<PendingCandidate> kume)
        {
            var sonuc = new List<PendingProtocol>();
            foreach (var group in kume.GroupBy(p => p.ProtokolId))
            {
                if (!protokoller.TryGetValue(group.Key, out var protokol)) continue;
                if (protokol.State == 0) continue;   // iptal edilmis protokol -- Is 3'un konusu

                // RECETE PROTOKOLLERI HIC GONDERILMIYOR (2026-09-29): gonderim tarafinda uc
                // ayri yerde atlaniyor ama tarama tarafinda filtrelenmiyordu, dolayisiyla
                // hicbir zaman yapilmayacak is ekranda birikiyordu.
                if (protokol.ProtokolTipiId == EncounterMapper.ReceteProtokolTipiId) continue;

                // TABAN TARIHTEN ONCEKILER LISTEYE HIC GIRMEZ.
                //
                // "Uygun degil" olarak isaretlemek yanlis olurdu: o protokoller listede
                // kalir, sayilari sisirir ve HICBIR ZAMAN uygun hale gelmez -- yani ekranda
                // hic yapilmayacak is birikir. Recete protokolleriyle ayni gerekce, ayni
                // cozum (iki satir yukarida).
                //
                // OLCUT ACILIS TARIHI (KULLANICI DUZELTMESI 2026-10-06: "devreye alim
                // tarihi 01.10.2026; bu tarih oncesindeki HICBIR kaydi gondermekle sorumlu
                // degilim").
                //
                // Ilk halde olcut uygunluk ani (kapanis/taburcu) idi ve "Eylul'de acilip
                // Ekim'de taburcu olan yatis gonderilir" diye yazilmisti -- kullanici tam
                // bu ornegi reddetti. Taban bir DEVREYE ALIM cizgisi: protokol devreye
                // alimdan once BASLADIYSA o epizot eski donemin isi, kapanisi sonraya
                // sarksa bile kapsam disi.
                //
                // BEDELI BILINCLI: devreye alim gununu asan uzun yatislar (orn. 25.09'da
                // yatan, 20.10'da taburcu olan hasta) hic gonderilmez. Kullanici kararina
                // gore bu dogru -- yatis devreye alimdan once basladi.
                //
                // AcilisTarihi bos olan protokoller BURADA elenmiyor; IsEligible onlari
                // zaten "Açılış tarihi yok" sebebiyle reddediyor.
                if (tabanTarih is { } taban && protokol.AcilisTarihi is { } acilis
                    && DateOnly.FromDateTime(acilis.Date) < taban) continue;

                var (eligible, reason) = IsEligible(protokol, openAfterDays, minYas);
                var items = group
                    .Select(c => new PendingItem(
                        c.ResourceType, c.Baslik, c.PusulaId, c.SonuclanmaTarihi, c.Aciklama,
                        sentLookup.GetValueOrDefault((c.ResourceType, c.PusulaId))))
                    .OrderBy(i => i.SonuclanmaTarihi)
                    .ToList();

                // SOGUMA: hepsi az once denenip basarisiz olduysa bu tur atla.
                if (eligible && sogumaDakika > 0 && items.Count > 0
                    && items.All(i => i.SonDeneme is { Status: SyncStatus.Failed } sd
                                      && (simdiUtc - sd.CreatedAtUtc).TotalMinutes < sogumaDakika))
                {
                    var gecen = (int)(simdiUtc - items.Max(i => i.SonDeneme!.CreatedAtUtc)).TotalMinutes;
                    eligible = false;
                    reason = $"Az önce denendi ({gecen} dk) -- {sogumaDakika} dakika dolmadan tekrar denenmiyor";
                }

                sonuc.Add(new PendingProtocol(protokol, items, eligible, reason));
            }
            return sonuc;
        }

        var gruplar = Grupla(bekleyen);
        var takilanGruplar = Grupla(takilan);

        // Ozet sayimlar TUM bekleyenler uzerinden (ekranda kirpilan liste degil) -- ve
        // yalnizca ANA kume uzerinden: takilanlarin sayisi buraya karismaz.
        var ozet = gruplar
            .SelectMany(g => g.Items)
            .GroupBy(i => i.Baslik)
            .ToDictionary(g => g.Key, g => g.Count());

        var takilanOzet = takilanGruplar
            .SelectMany(g => g.Items)
            .GroupBy(i => i.Baslik)
            .ToDictionary(g => g.Key, g => g.Count());

        // En eski bekleyen once -- en uzun sure takilı kalanlar gorunur olsun.
        static List<PendingProtocol> Kirp(List<PendingProtocol> g, int n) => g
            .OrderBy(EnEskiKalem)
            .Take(n)
            .ToList();

        // ANA KUMEDE UYGUN OLANLAR ONCE (2026-10-08 duzeltmesi).
        //
        // KULLANICI: "otomatik gonderimi sanki surekli yapmiyor gibi?"
        //
        // SEBEP: AutoSyncWorker bu listeden `Where(Eligible).Take(partiBoyutu)` aliyor, ama
        // liste n=200'de KIRPILIYORDU ve kirpma yalnizca "en eski kalem" sirasina bakiyordu.
        // Uygunluk sirayi hic etkilemedigi icin pencere, gonderilemeyecek protokollerle
        // doluyordu.
        //
        // OLCULDU (canli Pusula, 01.10-08.10 arasi): 634 protokol gonderime uygun, buna
        // karsilik 4.260'i "henuz uygun degil" (kapanmamis ayaktan protokoller -- ayaktanda
        // kapanis cogu zaman hic yapilmiyor, protokol acilistan 7 gun sonra uygun hale
        // geliyor) ve 85'i KALICI olarak uygun degil (taburcusu girilmemis yatan hasta).
        // Bu 4.345 protokolun kalemleri EN ESKI olanlar oldugu icin siranin basini onlar
        // tutuyor; 200'luk pencereye uygun protokol ya hic giremiyor ya da parti boyutunun
        // (50) cok altinda giriyordu. Dongu calisiyor, ama her turda gonderecek is
        // bulamiyordu.
        //
        // TAKILANLAR LISTESINE UYGULANMIYOR: orada soru "ne kadar suredir takili" --
        // uygunluk o listenin konusu degil, en eski once dogru sira.
        static List<PendingProtocol> KirpUygunOnce(List<PendingProtocol> g, int n) => g
            .OrderByDescending(x => x.Eligible)
            .ThenBy(EnEskiKalem)
            .Take(n)
            .ToList();

        static DateTime EnEskiKalem(PendingProtocol x) => x.Items.Count == 0
            ? DateTime.MaxValue
            : x.Items.Min(i => i.SonuclanmaTarihi == DateTime.MinValue ? DateTime.MaxValue : i.SonuclanmaTarihi);

        // EN ESKI BEKLEYEN -- KIRPILMAMIS kumeden. Artimli tarama penceresinin alt sinirini
        // bu belirliyor (bkz. RefreshIncrementalAsync); kirpilmis listeden hesaplansaydi,
        // ekrana sigmayan eski bir kalem pencerenin disinda kalir ve bir daha hic
        // gonderilmezdi.
        //
        // TAKILANLAR DA SAYILIYOR: pencerenin alt siniri onlari da kapsamali, yoksa gunluk
        // takilan taramasi kendi kayitlarini goremez hale gelir.
        DateTime? enEski = null;
        foreach (var g in gruplar.Concat(takilanGruplar))
            foreach (var i in g.Items)
                if (i.SonuclanmaTarihi != DateTime.MinValue && (enEski is null || i.SonuclanmaTarihi < enEski))
                    enEski = i.SonuclanmaTarihi;

        return new PendingWorkResult(
            KirpUygunOnce(gruplar, maxProtocols), ozet, gruplar.Count,
            enEski,
            Kirp(takilanGruplar, maxProtocols), takilanOzet, takilanGruplar.Count,
            kapsam);
    }

    // Bir kalemin durumu. KULLANICI KARARI (2026-10-03): "bekleyenleri ayrı bir listeye
    // alalım, onlar için ayrıca deneme yapsın, bizim ana gönderim listesine sayıları hiç
    // karışmasın."
    public enum KalemDurumu
    {
        Tamam,      // basariyla gonderilmis, yapacak bir sey yok
        Bekliyor,   // hic denenmemis ya da GECICI olarak basarisiz -- ana listede
        Takildi,    // mevcut veriyle gonderilemez ya da ust uste basarisiz -- ayri listede
    }

    // Ust uste bu kadar basarisizliktan sonra kalem "takildi" sayilir.
    //
    // ARTIK AYARDAN GELIYOR (KULLANICI KARARI 2026-10-05: "deneme panelinde ne yaziyorsa o
    // kadar denesin, sonrasinda takilanlar listesinde takip edelim"). Ayarlar'daki "Hata
    // Sonrasi Tekrar Deneme -> En fazla kac kez dene?" alani tam olarak bu esik; o alan
    // 2026-08'den beri kaydediliyor ama hicbir sey tarafindan OKUNMUYORDU.
    //
    // Bu sabit yalnizca VARSAYILAN. Neden 5 degil 3 degil ayardan: bir ag kesintisi ya da
    // bakanligin gecici hatasi birkac turda gecer; esigin uzerinde sebebin veride oldugunu
    // varsaymak makul. Yanilsak bile kayit kaybolmuyor -- takilanlar turunda yine deneniyor.
    public const int TakilmaEsigiVarsayilan = 3;

    // ATLANAN (Skipped) DOGRUDAN TAKILDI SAYILIR: Skipped, mapper'in "bu veriyle
    // gonderilemez" karari demektir (LOINC kodu yok, Icbari eslesmesi yok, sonuc degeri
    // bos). Bir sonraki turda ayni veriyle ayni sonuc cikar. Olculdu (30 gun): boyle bir
    // kalem 4.289 protokolde var -- protokollerin %21'i. Ana listede birakilsalardi, liste
    // en eskiden basladigi icin parti kontenjaninin tamamini kalici olarak isgal
    // ederlerdi ve hicbir yeni protokol gonderilemezdi.
    private static KalemDurumu Siniflandir(
        PendingCandidate c,
        Dictionary<(string, int), SyncLogEntry> sent,
        Dictionary<(string, int), int> ardisikBasarisiz,
        int takilmaEsigi)
    {
        if (!sent.TryGetValue((c.ResourceType, c.PusulaId), out var last))
            return KalemDurumu.Bekliyor;                     // hic denenmemis

        if (last.Status == SyncStatus.Skipped)
            return KalemDurumu.Takildi;

        if (last.Status != SyncStatus.Success)
        {
            var deneme = ardisikBasarisiz.GetValueOrDefault((c.ResourceType, c.PusulaId), 1);
            return deneme >= takilmaEsigi ? KalemDurumu.Takildi : KalemDurumu.Bekliyor;
        }

        // EPIKRIZ OZEL DURUMU (kullanici notu 2026-09-09: "epikrizde silinme yok, degisme
        // var"): basariyla gonderilmis olsa bile doktor metni sonradan duzeltmis olabilir.
        // Pusula YEREL saat tutar, SyncLog UTC -- karsilastirmadan once cevrilmeli, yoksa
        // 4 saatlik kayma olur (bkz. AzTime).
        if (c.ResourceType == "Composition" && c.SonuclanmaTarihi != DateTime.MinValue)
            return AzTime.ToUtc(c.SonuclanmaTarihi) > last.CreatedAtUtc
                ? KalemDurumu.Bekliyor
                : KalemDurumu.Tamam;

        return KalemDurumu.Tamam;
    }

    // Protokol gonderime uygun mu? Yatan ve ayaktan icin FARKLI kural (kullanici karari
    // 2026-09-09), ustune AYNI GUN GONDERME kurali (kullanici karari 2026-10-03).
    private static (bool Eligible, string? Reason) IsEligible(ProtokolListItem p, int openAfterDays, int minAgeDays)
    {
        var (uygunlukAni, redSebebi) = UygunlukAni(p, openAfterDays);
        if (uygunlukAni is null) return (false, redSebebi);

        // AYNI GUN GONDERILMEZ (KULLANICI KARARI 2026-10-03: "gönderim yaparken 1 gün
        // öncesini göndersin, hiç aynı gün göndermeyelim").
        //
        // NEDEN: protokol kapandigi gun kayit hala hareketli -- geciken laboratuvar sonucu
        // dusebilir, hekim epikrizi duzeltebilir, yanlis girilen bir islem silinebilir. O
        // anda gonderirsek bakanliga yarim bir tablo gider, sonra her degisiklik icin
        // guncelleme/iptal gondermek zorunda kaliriz. Bir gun beklemek kaydi oturtuyor.
        //
        // OLCUT GUN FARKI, 24 SAAT DEGIL: "1 gün öncesi" takvim gunu demek. 23:50'de kapanan
        // bir protokol ertesi gun 00:10'da degil, ertesi gun boyunca gonderilebilir olmali --
        // saat farkiyla ugrasmak kullanicinin kafasindaki kurali bozardi.
        var gunFarki = (DateTime.Today - uygunlukAni.Value.Date).Days;
        if (gunFarki < minAgeDays)
            return (false, minAgeDays == 1
                ? "Bugün kapandı -- aynı gün gönderilmiyor, yarın gönderilecek"
                : $"Kapanalı {gunFarki} gün oldu -- en az {minAgeDays} gün beklenmesi gerekiyor");

        return (true, null);
    }

    // Protokolun gonderime uygun hale geldigi AN. null ise henuz uygun degil (sebebiyle).
    private static (DateTime? An, string? Sebep) UygunlukAni(ProtokolListItem p, int openAfterDays)
    {
        // YATAN (Y): olcut TABURCU (kullanici karari 2026-09-14). Yatis haftalar surebilir ve
        // epizot bitmeden gondermek yanlis olur -- bu yuzden ayaktandaki gun esigi kisayolu
        // BURADA UYGULANMAZ.
        //
        // Tedavi.Yatis.TaburcuTarihi ONCELIKLI, protokolun KapanisTarihi'si yedek: birincisi
        // KLINIK taburcu ani, ikincisi IDARI kapanis (faturalama). Canli veride %89'unda ayni
        // gun, ama 471 protokolde (%9) idari kapanis 1-7 gun sonra -- yedege dusseydik o
        // kayitlar bir haftaya kadar gec giderdi. Yedek yine de duruyor cunku 180 gunluk
        // olcumde 2 protokolde tersi de goruldu: biri dolu digeri bos olabiliyor.
        if (p.GelisTipiId == "Y")
        {
            var taburcu = p.TaburcuTarihi ?? p.KapanisTarihi;
            if (taburcu is not null) return (taburcu, null);
            if (p.AcilisTarihi is null) return (null, "Açılış tarihi yok");

            // Veri girisi hatasiyla hic kapanmayan/taburcu edilmeyen protokoller sonsuza dek
            // beklemesin diye tavan.
            var yatanAcik = (DateTime.Now - p.AcilisTarihi.Value).TotalDays;
            return yatanAcik >= YatanMaxOpenDays
                ? (p.AcilisTarihi.Value.AddDays(YatanMaxOpenDays), null)
                : (null, $"Hasta hâlâ yatıyor (taburcu bekleniyor, {(int)yatanAcik} gündür açık)");
        }

        // AYAKTAN / GUNUBIRLIK: olcut protokolun kapanisi. Kapanmayan protokol orani yuksek
        // oldugundan (canli veride ayaktanin ~%20'si hic kapanmiyor) gun esigi kurali gecerli.
        if (p.KapanisTarihi is not null) return (p.KapanisTarihi, null);
        if (p.AcilisTarihi is null) return (null, "Açılış tarihi yok");

        var acik = (DateTime.Now - p.AcilisTarihi.Value).TotalDays;
        return acik >= openAfterDays
            ? (p.AcilisTarihi.Value.AddDays(openAfterDays), null)
            : (null, $"Protokol açık ({(int)acik} gündür) -- kapanması ya da {openAfterDays} gün beklenmesi gerekiyor");
    }
}

public record PendingItem(
    string ResourceType,
    string Baslik,
    int PusulaId,
    DateTime SonuclanmaTarihi,
    string? Aciklama,
    SyncLogEntry? SonDeneme);

public record PendingProtocol(
    ProtokolListItem Protokol,
    List<PendingItem> Items,
    bool Eligible,
    string? NotEligibleReason);

// Taramanin sonucu IKI AYRI KUME tasir (KULLANICI KARARI 2026-10-03). Protokoller/
// OzetSayimlar/ToplamProtokolSayisi yalnizca GONDERILEBILIR olanlari sayar; mevcut veriyle
// gonderilemeyecek kalemler Takilan* alanlarinda ve ana sayilara HIC karismiyor.
//
// Neden ayri: otomatik dongu en eskiden basladigi icin, gonderilemeyen kalemler ana listede
// kalsaydi parti kontenjaninin tamamini kalici olarak isgal eder ve hicbir yeni protokol
// gonderilemezdi. Ekranda da "4.289 bekleyen is" gibi hicbir zaman azalmayacak bir sayi
// gorunurdu -- sayfanin amaci neyin biriktigini gostermek, yanlis birikim onu bozar.
public record PendingWorkResult(
    List<PendingProtocol> Protokoller,
    Dictionary<string, int> OzetSayimlar,
    int ToplamProtokolSayisi,
    // Kirpilmamis kumedeki en eski bekleyen kalemin tarihi. Artimli tarama penceresinin
    // alt sinirini bu belirler -- null ise bekleyen is yok demektir.
    DateTime? EnEskiBekleyenTarih,
    List<PendingProtocol> TakilanProtokoller,
    Dictionary<string, int> TakilanOzetSayimlar,
    int ToplamTakilanProtokolSayisi,
    // KAPSAM (KULLANICI ISTEGI 2026-10-08): "bekleyen islerde hasta sayisini da yazalim,
    // kac hasta kaci gitmis vs."
    //
    // Sayfa bugune kadar yalnizca BEKLEYENI gosteriyordu: "412 bekleyen protokol" rakami
    // tek basina iyi mi kotu mu belli degildi -- 412/430 ise is daha baslamamis, 412/9.000
    // ise neredeyse bitmis demek. Payda olmadan pay anlamsiz.
    //
    // Birim PROTOKOL: gonderim birimi protokol, ve ayni hastanin aralikta birden fazla
    // protokolu olabiliyor. Ekranda "hasta" diye degil "protokol" diye yaziliyor ki
    // kullanici iki ayri sayiyi karistirmasin.
    PendingKapsam Kapsam)
{
    public static PendingWorkResult Bos => new([], new Dictionary<string, int>(), 0, null,
                                               [], new Dictionary<string, int>(), 0,
                                               PendingKapsam.Bos);
}

// Tarama penceresinin TAMAMI -- gonderilmisler dahil. Bekleyen sayilarinin paydasi.
//
// PROTOKOL UC DILIME AYRILIYOR (2026-10-08 duzeltmesi). Ilk halde yalnizca "tamamlanan"
// vardi ve kullanici hakli olarak sasirdi: ekranda 9 yaziyordu, "bir haftadir ne
// gonderiyoruz" diye sordu.
//
// Sebep olcunun KESKIN olmasiydi. "Tamamlanan" = protokolun TEK BIR kalemi bile
// eksik degil. Isleyen bir hastanede bu neredeyse hic gerceklesmiyor: protokol acik
// kaldigi surece yeni laboratuvar sonucu dusmeye devam ediyor, dolayisiyla 50 kaleminin
// 49'u gitmis bir protokol de "tamamlanmamis" sayiliyordu. Yani sayi, is yapilmadigini
// degil, isin bitmedigini olcuyordu -- ikisi ayni sey degil.
//
// Uc dilim gercegi gosteriyor: hic dokunulmamis protokol ile neredeyse bitmis protokol
// artik ayni kovada degil.
public record PendingKapsam(
    int TarananProtokol,
    int TamamlananProtokol,      // tek bir kalemi bile eksik degil
    int KismenGonderilmisProtokol, // bir kismi gitti, bir kismi bekliyor
    int HicGonderilmemisProtokol,  // hicbir kalemi gitmemis
    int TarananKalem, int GonderilmisKalem, int BekleyenKalem, int TakilanKalem)
{
    public static PendingKapsam Bos => new(0, 0, 0, 0, 0, 0, 0, 0);

    // ASIL ILERLEME OLCUSU KALEM DUZEYINDE. Protokol duzeyindeki "tamamlanan" keskin
    // oldugu icin ilerlemeyi oldugundan kotu gosteriyor; gonderilmis kalem orani ise
    // gercekten ne kadar is yapildigini soyluyor.
    public int GonderilmisKalemYuzde => TarananKalem == 0
        ? 100
        : (int)Math.Round(100.0 * GonderilmisKalem / TarananKalem);

    public int TamamlananYuzde => TarananProtokol == 0
        ? 100
        : (int)Math.Round(100.0 * TamamlananProtokol / TarananProtokol);
}
