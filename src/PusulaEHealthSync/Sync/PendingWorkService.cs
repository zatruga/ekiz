using System.Globalization;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// "Bekleyen Isler" -- Pusula'da HAZIR olup e-Health'e HENUZ GITMEMIS her sey.
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

        if (candidates.Count == 0)
            return new PendingWorkResult([], new Dictionary<string, int>(), 0, null);

        // 3) SyncLog'da ne var? Kaynak tipi bazinda toplu okuma.
        var sentLookup = new Dictionary<(string, int), SyncLogEntry>();
        foreach (var group in candidates.GroupBy(c => c.ResourceType))
        {
            var ids = group.Select(c => c.PusulaId).Distinct().ToList();
            // govdeleriGetir: false -- burada yalnizca DURUM'a bakiliyor (IsPending), gonderilen
            // FHIR govdesi hic kullanilmiyor. Tarama 54.000'i askin id ile calistigi icin bu iki
            // kolonu okumamak ciddi bellek/G-C tasarrufu (bkz. SyncLogStore).
            var sent = await syncLog.GetLatestByPusulaIdsAsync(group.Key, ids, ct, govdeleriGetir: false);
            foreach (var kv in sent) sentLookup[(group.Key, kv.Key)] = kv.Value;
        }

        // 4) Fark: gonderilmemis (ya da epikrizde: gonderildikten SONRA degismis) olanlar.
        var pending = candidates.Where(c => IsPending(c, sentLookup)).ToList();
        if (pending.Count == 0)
            return new PendingWorkResult([], new Dictionary<string, int>(), 0, null);

        // 5) Protokol bilgisi + uygunluk kurallari.
        var protokoller = await repository.GetProtokollerByIdsAsync(
            pending.Select(p => p.ProtokolId).Distinct().ToList(), ct);
        var openAfterDays = await settings.GetIntAsync(
            SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);

        var gruplar = new List<PendingProtocol>();
        foreach (var group in pending.GroupBy(p => p.ProtokolId))
        {
            if (!protokoller.TryGetValue(group.Key, out var protokol)) continue;
            if (protokol.State == 0) continue;   // iptal edilmis protokol -- Is 3'un konusu

            var (eligible, reason) = IsEligible(protokol, openAfterDays);
            var items = group
                .Select(c => new PendingItem(
                    c.ResourceType, c.Baslik, c.PusulaId, c.SonuclanmaTarihi, c.Aciklama,
                    sentLookup.GetValueOrDefault((c.ResourceType, c.PusulaId))))
                .OrderBy(i => i.SonuclanmaTarihi)
                .ToList();

            gruplar.Add(new PendingProtocol(protokol, items, eligible, reason));
        }

        // Ozet sayimlar TUM bekleyenler uzerinden (ekranda kirpilan liste degil).
        var ozet = gruplar
            .SelectMany(g => g.Items)
            .GroupBy(i => i.Baslik)
            .ToDictionary(g => g.Key, g => g.Count());

        var toplamProtokol = gruplar.Count;

        // En eski bekleyen once -- en uzun sure takilı kalanlar gorunur olsun.
        var kirpilmis = gruplar
            .OrderBy(g => g.Items.Min(i => i.SonuclanmaTarihi == DateTime.MinValue ? DateTime.MaxValue : i.SonuclanmaTarihi))
            .Take(maxProtocols)
            .ToList();

        // EN ESKI BEKLEYEN -- KIRPILMAMIS kumeden. Artimli tarama penceresini bu belirliyor
        // (bkz. RefreshIncrementalAsync); kirpilmis listeden hesaplansaydi, ekrana sigmayan
        // eski bir kalem pencerenin disinda kalir ve bir daha hic gonderilmezdi.
        DateTime? enEski = null;
        foreach (var g in gruplar)
            foreach (var i in g.Items)
                if (i.SonuclanmaTarihi != DateTime.MinValue && (enEski is null || i.SonuclanmaTarihi < enEski))
                    enEski = i.SonuclanmaTarihi;

        return new PendingWorkResult(kirpilmis, ozet, toplamProtokol, enEski);
    }

    // Bir kalem hala bekliyor mu?
    private static bool IsPending(PendingCandidate c, Dictionary<(string, int), SyncLogEntry> sent)
    {
        if (!sent.TryGetValue((c.ResourceType, c.PusulaId), out var last))
            return true;                                    // hic denenmemis

        if (last.Status != SyncStatus.Success)
            return true;                                    // denenmis ama basarisiz/atlanmis

        // EPIKRIZ OZEL DURUMU (kullanici notu 2026-09-09: "epikrizde silinme yok, degisme
        // var"): basariyla gonderilmis olsa bile doktor metni sonradan duzeltmis olabilir.
        // Pusula YEREL saat tutar, SyncLog UTC -- karsilastirmadan once cevrilmeli, yoksa
        // 4 saatlik kayma olur (bkz. AzTime).
        if (c.ResourceType == "Composition" && c.SonuclanmaTarihi != DateTime.MinValue)
            return AzTime.ToUtc(c.SonuclanmaTarihi) > last.CreatedAtUtc;

        return false;
    }

    // Protokol gonderime uygun mu? Yatan ve ayaktan icin FARKLI kural (kullanici karari
    // 2026-09-09).
    private static (bool Eligible, string? Reason) IsEligible(ProtokolListItem p, int openAfterDays)
    {
        // YATAN (Y): olcut TABURCU (kullanici karari 2026-09-14). Yatis haftalar surebilir ve
        // epizot bitmeden gondermek yanlis olur -- bu yuzden ayaktandaki gun esigi kisayolu
        // BURADA UYGULANMAZ.
        //
        // Tedavi.Yatis.TaburcuTarihi ONCELIKLI, protokolun KapanisTarihi'si yedek: birincisi
        // KLINIK taburcu ani, ikincisi IDARI kapanis (faturalama). Canli veride %89'unda ayni
        // gun, ama 471 protokolde (%9) idari kapanis 1-7 gun sonra -- yedege dusseydik o
        // kayitlar bir haftaya kadar gec giderdi. Yedek yine de duruyor cunku 180 gunluk
        // olcumde 2 protokolde tersi de gorulduy: biri dolu digeri bos olabiliyor.
        if (p.GelisTipiId == "Y")
        {
            var taburcu = p.TaburcuTarihi ?? p.KapanisTarihi;
            if (taburcu is not null) return (true, null);
            if (p.AcilisTarihi is null) return (false, "Açılış tarihi yok");

            // Veri girisi hatasiyla hic kapanmayan/taburcu edilmeyen protokoller sonsuza dek
            // beklemesin diye tavan.
            var yatanAcik = (DateTime.Now - p.AcilisTarihi.Value).TotalDays;
            return yatanAcik >= YatanMaxOpenDays
                ? (true, null)
                : (false, $"Hasta hâlâ yatıyor (taburcu bekleniyor, {(int)yatanAcik} gündür açık)");
        }

        // AYAKTAN / GUNUBIRLIK: olcut protokolun kapanisi. Kapanmayan protokol orani yuksek
        // oldugundan (canli veride ayaktanin ~%20'si hic kapanmiyor) gun esigi kurali gecerli.
        if (p.KapanisTarihi is not null) return (true, null);
        if (p.AcilisTarihi is null) return (false, "Açılış tarihi yok");

        var acik = (DateTime.Now - p.AcilisTarihi.Value).TotalDays;
        return acik >= openAfterDays
            ? (true, null)
            : (false, $"Protokol açık ({(int)acik} gündür) -- kapanması ya da {openAfterDays} gün beklenmesi gerekiyor");
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

public record PendingWorkResult(
    List<PendingProtocol> Protokoller,
    Dictionary<string, int> OzetSayimlar,
    int ToplamProtokolSayisi,
    // Kirpilmamis kumedeki en eski bekleyen kalemin tarihi. Artimli tarama penceresinin
    // alt sinirini bu belirler -- null ise bekleyen is yok demektir.
    DateTime? EnEskiBekleyenTarih);
