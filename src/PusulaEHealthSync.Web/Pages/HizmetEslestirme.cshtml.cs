using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Export;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Pusula hizmeti -> AZ az-procedure-codes eslestirmesi.
//
// KULLANICI KARARI (2026-09-24): ASIL ESLESTIRME PUSULA'DA YAPILACAK. Bu ekran
// calisma listesi ve oneri aracidir:
//   - Pusula'daki TUM hizmetler listelenir
//   - Eslesmesi Pusula'dan (Icbari Sigorta Fiyat Listesi) geliyorsa "Pusula" rozeti
//   - Bizim onerimiz varsa "Medigate" rozeti -- henuz Pusula'ya islenmemis TEKLIF
//   - Eslesmeyenler CHECKBOX ile secilip Excel'e aktarilir, eslestirme Pusula'da yapilir
public class HizmetEslestirmeModel(HizmetMappingStore store, SettingsStore settings,
    PusulaEHealthSync.Db.PusulaRepository pusulaRepository, ILogger<HizmetEslestirmeModel> logger) : PageModel
{
    public List<HizmetMappingStore.Satir> Satirlar { get; set; } = [];
    public List<string> Tipler { get; set; } = [];

    [BindProperty(SupportsGet = true)] public string Durum { get; set; } = "Tumu";
    [BindProperty(SupportsGet = true)] public string? Tip { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ara { get; set; }

    // LISTE SUZGECI VE SIRALAMA (KULLANICI ISTEGI 2026-10-28): "alt listede eslesen ve
    // eslesmeyenleri suzebilecegimiz sayi alani olsun, sutun basliklari siralansin".
    // Esles: ""=tumu, "evet", "hayir", "gonderilmez" -- ustteki Durum/Tip/Ara'nin
    // SONUCU icinde calisir, sayilari da o sonuctan (EslesTumu...).
    // Siralama SUNUCUDA: ekranda en fazla 400 satir var, tarayicida siralamak yalnizca
    // onlari siralardi. Excel de ayni sirayla iner.
    [BindProperty(SupportsGet = true)] public string? Esles { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sira { get; set; }
    [BindProperty(SupportsGet = true)] public string? Yon { get; set; }

    public int EslesTumu { get; set; }
    public int EslesEvet { get; set; }
    public int EslesHayir { get; set; }
    public int EslesGonderilmez { get; set; }
    public const int EkranSiniri = 400;

    // TERS GORUNUM (KULLANICI ISTEGI 2026-10-30): "Pusula-Icbari / Icbari-Pusula" dugmesi.
    // Varsayilan gorunum Pusula hizmetinden bakanlik koduna bakar; "icbari" gorunumu
    // bakanlik listesindeki (az-procedure-codes, 6.094 kod) her koddan Pusula'ya bakar:
    // bu koda baglanan bir Pusula hizmeti var mi. Ayni suzgecler, siralama ve Excel.
    [BindProperty(SupportsGet = true)] public string? Gorunum { get; set; }
    public bool IcbariGorunum => Gorunum == "icbari";

    // Grup/Tarif: resmi Xidmetler Zerfi (AzProcedureCodes.Grup/Tarif). PusulaFiyat: Pusula'daki
    // Icbari fiyat listesindeki fiyat(lar); kod listede yoksa bos, Pusula okunamadiysa null.
    public record KodSatiri(string Kod, string Ad, string? AdTr, List<HizmetMappingStore.Satir> Hizmetler, int OneriSayi,
        string? Grup, string? Tarif, decimal? TarifTutari, List<decimal>? PusulaFiyat)
    {
        public bool PusulaListesindeYok => PusulaFiyat is { Count: 0 };
        // Resmi tarif sayiysa ve Pusula'daki satirlarin HICBIRI ona esit degilse.
        public bool FiyatFarkli => TarifTutari is { } t && PusulaFiyat is { Count: > 0 } && !PusulaFiyat.Contains(t);
        public decimal? PusulaTutari => PusulaFiyat is { Count: > 0 } ? PusulaFiyat.Max() : null;

        // Eslesen: gonderilen (gonderilmez olmayan) en az bir Pusula hizmeti bu kodu tasiyor.
        // Yalniz gonderilmez: koda bagli hizmet var ama hepsi gonderilmez isaretli.
        public int GonderilenSayi => Hizmetler.Count(h => !h.Gonderilmez);
        public bool Eslesti => GonderilenSayi > 0;
        public bool YalnizGonderilmez => !Eslesti && Hizmetler.Count > 0;
        public bool Bekliyor => Hizmetler.Count == 0;
        public int Istem => Hizmetler.Where(h => !h.Gonderilmez).Sum(h => h.Istem);
    }

    // Bakanlik kodlari noktali sayilar (1.1.2, 1.1.10): parca parca SAYI olarak karsilastir,
    // yoksa metin sirasinda 1.1.10, 1.1.2'den once gelir. Sayi olmayan parca metin gibi.
    private static readonly Comparer<string> KodSirasi = Comparer<string>.Create((a, b) =>
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = int.TryParse(x[i], out var p) && int.TryParse(y[i], out var q)
                ? p.CompareTo(q) : string.Compare(x[i], y[i], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    });

    public List<KodSatiri> KodSatirlari { get; set; } = [];
    public int KodToplam { get; set; }
    public int KodEslesen { get; set; }
    public int KodEksik { get; set; }
    public int KodGonderilmez { get; set; }
    public int KodOneri { get; set; }
    public int KodKullanimda { get; set; }
    public int KodListedeYok { get; set; }
    public int KodFiyatFarkli { get; set; }
    public List<string> Gruplar { get; set; } = [];
    public bool FiyatListesiOkunamadi { get; set; }

    // Formlar ve yonlendirmeler arasinda tasinan filtre/sira durumu.
    public object Rota(bool saved = false) => new { saved, Durum, Tip, Ara, Esles, Sira, Yon, Gorunum };

    public static string KaynakAdi(HizmetMappingStore.Satir s) => s.Gonderilmez ? "gönderilmez" : s.Rozet switch
    {
        HizmetMappingStore.KaynakPusula => "Pusula",
        HizmetMappingStore.KaynakMedigate => "Medigate",
        HizmetMappingStore.KaynakKullanici => "elle",
        _ => "",
    };

    public bool Saved { get; set; }
    public int ToplamSayi { get; set; }
    public int PusulaSayi { get; set; }
    public int EksikSayi { get; set; }
    public int GonderilmezSayi { get; set; }
    public int ToplamIstem { get; set; }
    public int EslesenIstem { get; set; }
    public int GosterilenSayi { get; set; }
    public int KullanilanSayi { get; set; }
    public int EksikKullanilanSayi { get; set; }

    // Ad kurali ve "tibbi olmayabilir" suzgeci (bkz. HizmetKurali, Ayarlar > Hizmetler).
    public List<string> OnEkler { get; private set; } = [];
    public int KuralSayi { get; private set; }
    public int TibbiOlmayabilirSayi { get; private set; }
    [BindProperty(SupportsGet = true)] public int? Toplu { get; set; }

    public static string? AzAdi(string? kod) => AzProcedureCodes.Display(kod);
    public static string? AzAdiTr(string? kod) => AzProcedureCodes.DisplayTr(kod);

    // Pusula'daki bag canli okunamadiysa ekran gomulu katalogdaki (24.09) koda dustu.
    public bool BaglarOkunamadi { get; private set; }

    // Ad kurali uygulanmis tum hizmetler -- iki gorunum de buradan baslar.
    // IcbariKodu Pusula'dan CANLI (gonderimin kullandigi kod); okunamazsa gomulu katalog.
    private async Task<List<HizmetMappingStore.Satir>> HepsiAsync(CancellationToken ct)
    {
        var ham = await store.GetAllAsync(ct);
        try
        {
            var baglar = await pusulaRepository.GetIcbariBaglariAsync(ct);
            ham = [.. ham.Select(s => s with { IcbariKodu = baglar.GetValueOrDefault(s.HizmetId) ?? "" })];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BaglarOkunamadi = true;
            logger.LogWarning(ex, "Icbari baglari Pusula'dan okunamadi, gomulu katalog kullaniliyor");
        }
        OnEkler = await PusulaEHealthSync.Sync.HizmetKurali.OnEkleriAsync(settings, ct);
        var hepsi = ham.Select(s => !s.Gonderilmez && PusulaEHealthSync.Sync.HizmetKurali.OnEkUyar(s.HizmetAdi, OnEkler)
                ? s with { Gonderilmez = true, Kural = true } : s).ToList();
        KuralSayi = hepsi.Count(s => s.Kural);
        return hepsi;
    }

    // ICBARI -> PUSULA: bakanlik listesinin her kodu ve ona bagli Pusula hizmetleri.
    // Hizmetin kodu EtkinKod (Pusula'daki Icbari kodu ya da elle girilen); Medigate
    // onerisi eslesme SAYILMAZ, yalnizca "onerisi var" suzgecinde gorunur.
    private async Task<List<KodSatiri>> KodlariFiltreleAsync(CancellationToken ct)
    {
        var hepsi = await HepsiAsync(ct);
        var kodaGore = hepsi.Where(s => s.EtkinKod is not null)
            .ToLookup(s => AzProcedureCodes.Anahtar(s.EtkinKod!), StringComparer.OrdinalIgnoreCase);
        var oneriler = hepsi.Where(s => s.Bekliyor && !string.IsNullOrWhiteSpace(s.Oneri))
            .GroupBy(s => AzProcedureCodes.Anahtar(s.Oneri!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Pusula fiyat listesi canli okunur (salt-okunur SELECT). Okunamazsa ekran calismaya
        // devam eder, yalniz fiyat sutunu ve fiyat kutulari bos kalir.
        Dictionary<string, List<decimal>>? fiyatlar = null;
        try { fiyatlar = await pusulaRepository.GetIcbariFiyatListesiAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FiyatListesiOkunamadi = true;
            logger.LogWarning(ex, "Icbari fiyat listesi Pusula'dan okunamadi");
        }

        var kodlar = AzProcedureCodes.Hepsi
            .Select(k => new KodSatiri(k.Key, k.Value, AzProcedureCodes.DisplayTr(k.Key),
                [.. kodaGore[k.Key].OrderByDescending(h => h.Istem)], oneriler.GetValueOrDefault(k.Key),
                AzProcedureCodes.Grup(k.Key), AzProcedureCodes.Tarif(k.Key), AzProcedureCodes.TarifTutari(k.Key),
                fiyatlar is null ? null : fiyatlar.GetValueOrDefault(k.Key) ?? []))
            .OrderBy(k => k.Kod, KodSirasi)
            .ToList();

        KodToplam = kodlar.Count;
        KodEslesen = kodlar.Count(k => k.Eslesti);
        KodEksik = kodlar.Count(k => k.Bekliyor);
        KodGonderilmez = kodlar.Count(k => k.YalnizGonderilmez);
        KodOneri = kodlar.Count(k => k.Bekliyor && k.OneriSayi > 0);
        KodKullanimda = kodlar.Count(k => k.Istem > 0);
        KodListedeYok = kodlar.Count(k => k.PusulaListesindeYok);
        KodFiyatFarkli = kodlar.Count(k => k.FiyatFarkli);
        Gruplar = [.. kodlar.Select(k => k.Grup).OfType<string>().Distinct().OrderBy(g => g)];

        var liste = Durum switch
        {
            "ListedeYok" => kodlar.Where(k => k.PusulaListesindeYok),
            "FiyatFarki" => kodlar.Where(k => k.FiyatFarkli),
            "Pusula" => kodlar.Where(k => k.Eslesti),
            "Eksik" => kodlar.Where(k => k.Bekliyor),
            "Gonderilmez" => kodlar.Where(k => k.YalnizGonderilmez),
            "Oneri" => kodlar.Where(k => k.Bekliyor && k.OneriSayi > 0),
            "Kullanimda" => kodlar.Where(k => k.Istem > 0),
            _ => kodlar.AsEnumerable(),
        };

        // Bu gorunumde Tip = resmi xidmet qrupu (Pusula gorunumunde hizmet tipi).
        if (!string.IsNullOrWhiteSpace(Tip))
            liste = liste.Where(k => k.Grup == Tip);

        if (!string.IsNullOrWhiteSpace(Ara))
        {
            var q = Ara.Trim();
            liste = liste.Where(k =>
                k.Kod.Contains(q, StringComparison.OrdinalIgnoreCase)
                || k.Ad.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (k.AdTr ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || k.Hizmetler.Any(h => h.HizmetAdi.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || h.HizmetKodu.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        var ara = liste.ToList();
        EslesTumu = ara.Count;
        EslesEvet = ara.Count(k => k.Eslesti);
        EslesHayir = ara.Count(k => k.Bekliyor);
        EslesGonderilmez = ara.Count(k => k.YalnizGonderilmez);
        IEnumerable<KodSatiri> sonuc = Esles switch
        {
            "evet" => ara.Where(k => k.Eslesti),
            "hayir" => ara.Where(k => k.Bekliyor),
            "gonderilmez" => ara.Where(k => k.YalnizGonderilmez),
            _ => ara,
        };

        if (string.IsNullOrEmpty(Sira)) return [.. sonuc];
        var tr = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true);
        var azalan = Yon == "desc";
        IOrderedEnumerable<KodSatiri> Metin(Func<KodSatiri, string?> f) =>
            azalan ? sonuc.OrderByDescending(k => f(k) ?? "", tr) : sonuc.OrderBy(k => f(k) ?? "", tr);
        IOrderedEnumerable<KodSatiri> Sayi(Func<KodSatiri, int> f) =>
            azalan ? sonuc.OrderByDescending(f) : sonuc.OrderBy(f);
        return Sira switch
        {
            "kod" => azalan ? [.. sonuc.OrderByDescending(k => k.Kod, KodSirasi)] : [.. sonuc.OrderBy(k => k.Kod, KodSirasi)],
            "ad" => [.. Metin(k => k.AdTr ?? k.Ad)],
            "hizmet" => [.. Sayi(k => k.Hizmetler.Count)],
            "istem" => [.. Sayi(k => k.Istem)],
            "tarif" => azalan ? [.. sonuc.OrderByDescending(k => k.TarifTutari ?? -1)] : [.. sonuc.OrderBy(k => k.TarifTutari ?? decimal.MaxValue)],
            "fiyat" => azalan ? [.. sonuc.OrderByDescending(k => k.PusulaTutari ?? -1)] : [.. sonuc.OrderBy(k => k.PusulaTutari ?? decimal.MaxValue)],
            _ => [.. sonuc],
        };
    }

    private async Task<List<HizmetMappingStore.Satir>> FiltreleAsync(CancellationToken ct)
    {
        var hepsi = await HepsiAsync(ct);

        // AD KURALI (HepsiAsync): adi on eklerden biriyle baslayan hizmet "gonderilmez" sayilir --
        // gonderim, tarama ve Icbari karti ayni kurali Pusula sorgusunda uyguluyor (HizmetKurali).

        // TIBBI OLMAYABILIR: tam sozcuk eslesmesi, yalnizca henuz gonderilmez olmayanlar.
        var desen = PusulaEHealthSync.Sync.HizmetKurali.SozcukDeseni(
            await PusulaEHealthSync.Sync.HizmetKurali.SozcukleriAsync(settings, ct));
        bool Aday(HizmetMappingStore.Satir s) => !s.Gonderilmez && desen is not null && desen.IsMatch(s.HizmetAdi);
        TibbiOlmayabilirSayi = hepsi.Count(Aday);

        // TEK KURAL (2026-10-28): Eslesen = kodu var ve gonderilmez degil; Eslesme yok = kodu
        // yok ve gonderilmez degil. Eslesen + Eslesme yok + Gonderilmez = Tum hizmetler, ve
        // alttaki liste suzgeci (Esles) ayni ozelliklerden sayiyor.
        ToplamSayi = hepsi.Count;
        PusulaSayi = hepsi.Count(s => s.Eslesti);
        GonderilmezSayi = hepsi.Count(s => s.Gonderilmez);
        EksikSayi = hepsi.Count(s => s.Bekliyor);
        KullanilanSayi = hepsi.Count(s => s.Istem > 0);
        EksikKullanilanSayi = hepsi.Count(s => s.Istem > 0 && s.Bekliyor);
        ToplamIstem = hepsi.Sum(s => s.Istem);
        EslesenIstem = hepsi.Where(s => s.Eslesti).Sum(s => s.Istem);
        Tipler = [.. hepsi.Select(s => s.HizmetTipi).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().OrderBy(t => t)];

        var liste = Durum switch
        {
            "Pusula" => hepsi.Where(s => s.Eslesti),
            "Eksik" => hepsi.Where(s => s.Bekliyor),
            "Gonderilmez" => hepsi.Where(s => s.Gonderilmez),
            // Katalogda 20.613 hizmet var ama 17.008'i son bir yilda HIC istenmemis.
            // Bu filtre gercekten kullanilan kalemlere odaklanmayi sagliyor.
            "Kullanimda" => hepsi.Where(s => s.Istem > 0),
            "KullanimdaEksik" => hepsi.Where(s => s.Istem > 0 && s.Bekliyor),
            "TibbiOlmayabilir" => hepsi.Where(Aday),
            _ => hepsi.AsEnumerable(),
        };

        if (!string.IsNullOrWhiteSpace(Tip))
            liste = liste.Where(s => s.HizmetTipi == Tip);

        if (!string.IsNullOrWhiteSpace(Ara))
        {
            var q = Ara.Trim();
            liste = liste.Where(s =>
                s.HizmetAdi.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.HizmetKodu.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.IcbariKodu.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.AzKod ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.Oneri ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var ara = liste.ToList();
        EslesTumu = ara.Count;
        EslesEvet = ara.Count(s => s.Eslesti);
        EslesHayir = ara.Count(s => s.Bekliyor);
        EslesGonderilmez = ara.Count(s => s.Gonderilmez);
        IEnumerable<HizmetMappingStore.Satir> sonuc = Esles switch
        {
            "evet" => ara.Where(s => s.Eslesti),
            "hayir" => ara.Where(s => s.Bekliyor),
            "gonderilmez" => ara.Where(s => s.Gonderilmez),
            _ => ara,
        };
        return Sirala(sonuc);
    }

    private List<HizmetMappingStore.Satir> Sirala(IEnumerable<HizmetMappingStore.Satir> liste)
    {
        // Varsayilan: magazanin sirasi (istem cok olandan aza).
        if (string.IsNullOrEmpty(Sira)) return [.. liste];
        var tr = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true);
        var azalan = Yon == "desc";
        IOrderedEnumerable<HizmetMappingStore.Satir> Metin(Func<HizmetMappingStore.Satir, string?> f) =>
            azalan ? liste.OrderByDescending(s => f(s) ?? "", tr) : liste.OrderBy(s => f(s) ?? "", tr);
        IOrderedEnumerable<HizmetMappingStore.Satir> Sayi(Func<HizmetMappingStore.Satir, int> f) =>
            azalan ? liste.OrderByDescending(f) : liste.OrderBy(f);
        return Sira switch
        {
            "id" => [.. Sayi(s => s.HizmetId)],
            "kod" => [.. Metin(s => s.HizmetKodu)],
            "ad" => [.. Metin(s => s.HizmetAdi)],
            "tip" => [.. Metin(s => s.HizmetTipi)],
            "istem" => [.. Sayi(s => s.Istem)],
            "azkod" => [.. Metin(s => s.EtkinKod)],
            "kaynak" => [.. Metin(KaynakAdi)],
            _ => [.. liste],
        };
    }

    public async Task OnGetAsync(bool saved = false, CancellationToken ct = default)
    {
        Saved = saved;
        if (IcbariGorunum)
        {
            var kodlar = await KodlariFiltreleAsync(ct);
            GosterilenSayi = kodlar.Count;
            KodSatirlari = [.. kodlar.Take(EkranSiniri)];
            return;
        }
        var liste = await FiltreleAsync(ct);
        GosterilenSayi = liste.Count;
        // 20.613 satiri birden cizmek sayfayi agirlastiriyor -- hacme gore ilk 400,
        // daraltmak icin filtreler var. Excel'e aktarim TUM filtre sonucunu alir.
        Satirlar = [.. liste.Take(EkranSiniri)];
    }

    public async Task<IActionResult> OnPostKaydetAsync(int hizmetId, string? azKod, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, azKod, gonderilmez: false, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    public async Task<IActionResult> OnPostGonderilmezAsync(int hizmetId, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, null, gonderilmez: true, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    // TOPLU ISARETLEME (2026-10-28): listede secilen satirlarin hepsi "gonderilmez". Kurala
    // zaten uyanlar atlanir (onlar ad kuralindan geliyor, ayrica isaret gerekmiyor).
    public async Task<IActionResult> OnPostTopluGonderilmezAsync(int[]? secili, CancellationToken ct)
    {
        if (secili is not { Length: > 0 }) return RedirectToPage("/HizmetEslestirme", Rota());
        var onEkler = await PusulaEHealthSync.Sync.HizmetKurali.OnEkleriAsync(settings, ct);
        var mevcut = (await store.GetAllAsync(ct)).ToDictionary(s => s.HizmetId);
        var n = 0;
        foreach (var id in secili.Distinct())
        {
            if (!mevcut.TryGetValue(id, out var s) || s.Gonderilmez
                || PusulaEHealthSync.Sync.HizmetKurali.OnEkUyar(s.HizmetAdi, onEkler)) continue;
            await store.SetAsync(id, s.AzKod, gonderilmez: true, ct);
            n++;
        }
        return RedirectToPage("/HizmetEslestirme", new { Toplu = n, Durum, Tip, Ara, Esles, Sira, Yon, Gorunum });
    }

    // "Gonderilmez" isaretini kaldirir; varsa elle girilmis kod korunur.
    public async Task<IActionResult> OnPostGonderilsinAsync(int hizmetId, CancellationToken ct)
    {
        var mevcut = (await store.GetAllAsync(ct)).FirstOrDefault(s => s.HizmetId == hizmetId);
        await store.SetAsync(hizmetId, mevcut?.AzKod, gonderilmez: false, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    public async Task<IActionResult> OnPostSifirlaAsync(int hizmetId, CancellationToken ct)
    {
        await store.SifirlaAsync(hizmetId, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    /// <summary>
    /// Secilen hizmetleri Excel'e aktarir. Dosya Pusula'da eslestirme yapmak icin
    /// kullanilacak: son sutun BOS birakiliyor, oraya Pusula'ya girilen kod yazilir.
    /// Hicbiri secilmemisse mevcut filtrenin TAMAMI aktarilir.
    /// </summary>
    public async Task<IActionResult> OnPostExcelAsync(int[]? secili, string[]? seciliKod, CancellationToken ct)
    {
        if (IcbariGorunum) return await KodExcelAsync(seciliKod, ct);
        var liste = await FiltreleAsync(ct);
        if (secili is { Length: > 0 })
        {
            var küme = secili.ToHashSet();
            liste = [.. liste.Where(s => küme.Contains(s.HizmetId))];
        }

        var sutunlar = new List<XlsxWriter.Sutun>
        {
            new("Pusula Hizmet Id", 16, Sayi: true),
            new("Pusula Hizmet Kodu", 20),
            new("Hizmet Adı", 52),
            new("Hizmet Tipi", 22),
            new("İstem (365 gün)", 16, Sayi: true),
            new("Pay %", 10, Sayi: true),
            new("Kümülatif %", 13, Sayi: true),
            new("Mevcut Eşleşme (İcbari)", 22),
            new("Önerilen Bakanlık Kodu", 22),
            new("Öneri Kaynağı", 15),
            new("Bakanlık Adı (AZ)", 62),
            new("Bakanlık Adı (TR)", 62),
            new("▶ PUSULA'YA GİRİLEN KOD", 26),
        };

        // PAY VE KUMULATIF PAY (2026-10-24, kullanici: "kullanilan ama eslesmesi olmayan
        // populer hizmetlerin listesi -- bu oranlara gore eslestirme yaptirmaliyim").
        // Payda: indirilen listenin toplam istemi. "Kullanimda + eslesme yok" filtresiyle
        // indirildiginde kumulatif sutun "ilk N hizmet eslesirse eksigin yuzde kaci
        // kapanir" sorusunu dogrudan cevapliyor. Varsayilan sira istem sirasi; baslikla
    // baska siralanmissa kumulatif o sirayi izler.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var toplamIstem = Math.Max(1, liste.Sum(s => s.Istem));
        var birikim = 0;
        var satirlar = liste.Select(s =>
        {
            var kod = s.EtkinKod;
            birikim += s.Istem;
            return new string?[]
            {
                s.HizmetId.ToString(inv),
                s.HizmetKodu,
                s.HizmetAdi,
                s.HizmetTipi,
                s.Istem.ToString(inv),
                (s.Istem * 100.0 / toplamIstem).ToString("0.00", inv),
                (birikim * 100.0 / toplamIstem).ToString("0.0", inv),
                s.IcbariKodu,
                s.AzKod ?? s.Oneri,
                KaynakAdi(s),
                AzAdi(kod),
                AzAdiTr(kod),
                "",
            };
        }).ToList();   // birikim sirali sayaca dayaniyor -- tek seferde, sirayla uretilmeli

        var bayt = XlsxWriter.Olustur("Hizmet Eşleştirme", sutunlar, satirlar);
        var ad = $"hizmet-eslestirme-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ad);
    }

    // Icbari -> Pusula listesi Excel'i: kod basina bir satir, bagli Pusula hizmetleri tek hucrede.
    private async Task<IActionResult> KodExcelAsync(string[]? seciliKod, CancellationToken ct)
    {
        var liste = await KodlariFiltreleAsync(ct);
        if (seciliKod is { Length: > 0 })
        {
            var küme = seciliKod.ToHashSet(StringComparer.OrdinalIgnoreCase);
            liste = [.. liste.Where(k => küme.Contains(k.Kod))];
        }

        var sutunlar = new List<XlsxWriter.Sutun>
        {
            new("Bakanlık Kodu", 16),
            new("Bakanlık Adı (AZ)", 62),
            new("Bakanlık Adı (TR)", 62),
            new("Xidmət Qrupu", 34),
            new("Resmi Tarif (₼)", 16, Sayi: true),
            new("Resmi Tarif (metin)", 30),
            new("Pusula Fiyatı (₼)", 16, Sayi: true),
            new("Fiyat Farkı (resmi − Pusula)", 18, Sayi: true),
            new("Pusula Fiyat Listesinde", 14),
            new("Durum", 18),
            new("Pusula Hizmet Sayısı", 14, Sayi: true),
            new("İstem (365 gün)", 16, Sayi: true),
            new("Pusula Hizmetleri (Id · Kod · Ad)", 80),
            new("Medigate Önerisi (hizmet)", 16, Sayi: true),
        };
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var satirlar = liste.Select(k => new string?[]
        {
            k.Kod,
            k.Ad,
            k.AdTr,
            k.Grup,
            k.TarifTutari?.ToString(inv),
            k.TarifTutari is null ? k.Tarif : null,
            k.PusulaFiyat is { Count: > 0 } pf ? string.Join(" / ", pf.Select(f => f.ToString(inv))) : null,
            k.FiyatFarkli ? (k.TarifTutari!.Value - k.PusulaTutari!.Value).ToString(inv) : null,
            k.PusulaFiyat is null ? "okunamadı" : k.PusulaListesindeYok ? "yok" : "var",
            k.Eslesti ? "Eşleşen" : k.YalnizGonderilmez ? "Yalnız gönderilmez" : "Pusula karşılığı yok",
            k.Hizmetler.Count.ToString(inv),
            k.Istem.ToString(inv),
            string.Join(" | ", k.Hizmetler.Select(h => $"{h.HizmetId} · {h.HizmetKodu} · {h.HizmetAdi}{(h.Gonderilmez ? " (gönderilmez)" : "")}")),
            k.OneriSayi.ToString(inv),
        }).ToList();

        var bayt = XlsxWriter.Olustur("İcbari-Pusula", sutunlar, satirlar);
        var ad = $"icbari-pusula-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ad);
    }
}
