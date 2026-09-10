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
    public const int DefaultScanDays = 60;

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

    public async Task<PendingWorkResult> RefreshAsync(
        int scanDays = DefaultScanDays, int maxProtocols = 200, CancellationToken ct = default)
    {
        // Ayni anda iki tarama baslamasin (sayfa + dongu ayni saniyede tetiklerse).
        await _refreshLock.WaitAsync(ct);
        try
        {
            var sonuc = await GetPendingAsync(scanDays, maxProtocols, ct);
            SonSonuc = sonuc;
            SonHesaplamaUtc = DateTime.UtcNow;
            return sonuc;
        }
        finally { _refreshLock.Release(); }
    }

    public async Task<PendingWorkResult> GetPendingAsync(
        int scanDays = DefaultScanDays, int maxProtocols = 200, CancellationToken ct = default)
    {
        var fromLocal = DateTime.Now.Date.AddDays(-scanDays);

        // 1) Pusula'da hazir olan her sey. Her kaynak KENDI sonuclanma tarihiyle taranir.
        var candidates = new List<PendingCandidate>();
        candidates.AddRange(await repository.GetCompletedLabResultsAsync(fromLocal, ct));
        candidates.AddRange(await repository.GetCompletedRadiologyAsync(fromLocal, ct));
        candidates.AddRange(await repository.GetCompletedPathologyAsync(fromLocal, ct));
        candidates.AddRange(await repository.GetCreatedProceduresAsync(fromLocal, ct));
        candidates.AddRange(await repository.GetLockedEpikrizAsync(fromLocal, ct));

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
            return new PendingWorkResult([], new Dictionary<string, int>(), 0);

        // 3) SyncLog'da ne var? Kaynak tipi bazinda toplu okuma.
        var sentLookup = new Dictionary<(string, int), SyncLogEntry>();
        foreach (var group in candidates.GroupBy(c => c.ResourceType))
        {
            var ids = group.Select(c => c.PusulaId).Distinct().ToList();
            var sent = await syncLog.GetLatestByPusulaIdsAsync(group.Key, ids, ct);
            foreach (var kv in sent) sentLookup[(group.Key, kv.Key)] = kv.Value;
        }

        // 4) Fark: gonderilmemis (ya da epikrizde: gonderildikten SONRA degismis) olanlar.
        var pending = candidates.Where(c => IsPending(c, sentLookup)).ToList();
        if (pending.Count == 0)
            return new PendingWorkResult([], new Dictionary<string, int>(), 0);

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

        return new PendingWorkResult(kirpilmis, ozet, toplamProtokol);
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
        if (p.KapanisTarihi is not null) return (true, null);
        if (p.AcilisTarihi is null) return (false, "Açılış tarihi yok");

        var acik = (DateTime.Now - p.AcilisTarihi.Value).TotalDays;

        // YATAN (Y): taburcu beklenir. Yatis haftalar surebilir ve epizot bitmeden gondermek
        // yanlis olur -- bu yuzden ayaktandaki 7 gun kisayolu BURADA UYGULANMAZ. Tek istisna,
        // veri girisi hatasiyla hic kapanmayan protokoller icin 90 gunluk tavan.
        if (p.GelisTipiId == "Y")
            return acik >= YatanMaxOpenDays
                ? (true, null)
                : (false, $"Hasta hâlâ yatıyor (taburcu bekleniyor, {(int)acik} gündür açık)");

        // AYAKTAN / GUNUBIRLIK: kapanmayan protokol orani yuksek oldugundan (canli veride
        // ayaktanin ~%20'si hic kapanmiyor) mevcut gun esigi kurali gecerli.
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
    int ToplamProtokolSayisi);
