using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Teknik aktivite akisi -- her senkron denemesinin ham kaydi (SyncLog). Protokol
// Listesi ana ekran olduktan sonra bu sayfa ikincil katman: "gonderilen her seyin
// duz listesi" gerektiginde (ozellikle Patient disi kayit turleri eklendikce) burasi
// kullanilir.
//
// KULLANICI ISTEGI (2026-08-25): "ust bardaki sayilarin ustune tarih koyalim, tarihe
// gore listelensin, bilgi bari da secim yapilabilir olsun" -- Index.cshtml'deki From/To
// tarih araligi deseni + Doktorlar/BolumEslestirme'deki tiklanabilir stat deseni buraya
// da uygulandi.
//
// PROTOKOLE GORE GRUPLAMA (KULLANICI ISTEGI 2026-10-05): "gonderim akisinda tek tek tum
// islemler ve gonderimler icin tek tek satir yapmasin, protokole gore gruplayip yapsin,
// protokol bilgisi vs gosterilsin."
//
// HASTA BAZLI SAYFALAMA (KULLANICI ISTEGI 2026-10-07): "neden bu liste 2 hasta
// gosteriyor, sonraki dedikce yukaridaki sayilar degisiyor; hasta sayilari tam gelsin,
// sayfa listesindeki hizmetlere gore degil hastayi saysin."
//
// IKI AYRI SIKAYET, IKI AYRI SEBEP:
//
//   1. "2 hasta gorunuyor" -- SAYFALAMANIN BIRIMI YANLISTI. Sayfa 60 SATIR cekiyor,
//      ekran onlari protokole gore grupluyordu. Tek bir yatan hastanin bir gunluk
//      laboratuvari 60 satiri tek basina doldurabildigi icin 7.554 denemelik bir gun
//      listede 2 hasta olarak gorunuyordu. Sayfa boyutunu buyutmek bunu cozmez; birim
//      artik GRUP (hasta/protokol), satir degil.
//
//   2. "sonraki dedikce sayilar degisiyor" -- ust karttaki sayilar sayfadan BAGIMSIZ
//      hesaplaniyordu (tarih araliginin tamami), yani sayfaya gore degismiyorlardi.
//      Degismelerinin sebebi otomatik gonderimin O SIRADA yazmaya devam etmesiydi: iki
//      sayfa arasinda gecen saniyelerde SyncLog'a yeni satirlar giriyordu. Artik aralik
//      ozeti bir kez hesaplanip 2 dakika onbellekte tutuluyor, yani sayfalar arasinda
//      gezinirken sayilar SABIT kaliyor (ayrica her sayfada on binlerce id'yi Pusula'ya
//      yeniden sormayi da onluyor).
public class AktiviteModel(SyncLogStore syncLog, PusulaRepository repository,
    IMemoryCache cache, ILogger<AktiviteModel> logger) : PageModel
{
    public List<SyncLogEntry> Entries { get; set; } = [];
    public List<AktiviteGrubu> Gruplar { get; set; } = [];

    // (ResourceType, PusulaId) -> gonderilen kaydin insan okunur adi.
    // KULLANICI ISTEGI (2026-10-06): "gonderilen veriyi de yazsin -- doktor ise brans ve
    // adi, islem/tetkik ise adlari." SyncLog yalnizca id tutuyor; "Tetkik 7612934" teknik
    // olarak dogru ama okuyana hicbir sey anlatmiyor.
    //
    // YALNIZCA SAYFADAKI SATIRLAR icin cozuluyor: bu sorgular join'li ve pahali, araligin
    // tamami icin calistirmanin anlami yok (ekranda gorunmeyecekler).
    public Dictionary<(string, int), string> Aciklamalar { get; set; } = new();

    public int PageNumber { get; set; }
    public bool HasNextPage { get; set; }

    // Sayfalama bilgisi ("... hastanin 26-50 arasi gosteriliyor"). Birim GRUP, satir degil.
    public int IlkSira => (PageNumber - 1) * PageSize + 1;
    public int SonSira => IlkSira + Gruplar.Count - 1;

    // Ust kart sayilari -- ARALIGIN TAMAMI, sayfadan bagimsiz.
    public AkisOzeti Ozet { get; set; } = AkisOzeti.Bos;

    // SAYFA BOYUTU ARTIK GRUP SAYISI (hasta/protokol), satir sayisi degil. 25 grup, bir
    // ekranda kapali halde rahat sigiyor; acildiginda satir sayisi gruba gore degisiyor
    // ama bu kullanicinin zaten gormek istedigi derinlik.
    private const int PageSize = 25;

    // Aralikta okunacak azami satir -- bellek sigortasi. Mevcut hacimde (gunde ~7.500
    // deneme) yaklasik sekiz gunluk aralik demek; varsayilan aralik yedi gun. Asilirsa
    // kullanici UYARILIYOR, sessizce eksik sayi gosterilmiyor.
    private const int OzetTavani = 60_000;

    // Onbellek omru. Kisa: bu ekran "simdi ne oluyor" diye aciliyor, bayat veri ise
    // yarar. Uzun: sayfalar arasi gezinirken sayilar oynamasin ve Pusula cozumu her
    // sayfada tekrarlanmasin. Iki dakika ikisini de kabul edilebilir kiliyor.
    private static readonly TimeSpan OnbellekOmru = TimeSpan.FromMinutes(2);

    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ResourceType { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateOnly? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int P { get; set; } = 1;

    // Genel Bakış'taki "Hata Kategorileri" kutucuklarından geliyor -- SyncLogEntry.ErrorCategory
    // ile ayni etiketle eslesen Failed kayitlarini gosterir. Bu bir DB sutunu degil (mesaj
    // metninden turetiliyor), o yuzden SQL'de degil, bellek icinde filtreleniyor.
    [BindProperty(SupportsGet = true)]
    public string? Kategori { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly EffectiveTo { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? today.AddDays(-6);
        EffectiveTo = To ?? today;
        PageNumber = P < 1 ? 1 : P;

        // Filtredeki tarihler YEREL gun (kullanici takvimden secer), SyncLog ise UTC saklar --
        // cevrim sart. Eskiden yerel gece yarisi dogrudan UTC alaniyla karsilastiriliyordu,
        // bu da gun sinirini fiilen 04:00'e kaydiriyordu (bkz. AzTime).
        var fromUtc = AzTime.ToUtc(EffectiveFrom.ToDateTime(TimeOnly.MinValue));
        var toUtcExclusive = AzTime.ToUtc(EffectiveTo.ToDateTime(TimeOnly.MinValue).AddDays(1));

        Ozet = await OzetAlAsync(fromUtc, toUtcExclusive, ct);

        // ---- SAYFAYA DUSEN GRUPLAR ----
        var sayfaGruplari = Ozet.Gruplar.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        HasNextPage = Ozet.Gruplar.Count > PageNumber * PageSize;

        // Govdeler YALNIZCA bu gruplar icin okunuyor (bkz. SyncLogStore.GetByIdsAsync).
        var sayfaIdleri = sayfaGruplari.SelectMany(g => g.KayitIdleri).ToList();
        Entries = await syncLog.GetByIdsAsync(sayfaIdleri, ct);

        var kayitlar = Entries.ToDictionary(e => e.Id);
        Gruplar = sayfaGruplari
            .Select(g => new AktiviteGrubu(
                g.ProtokolId,
                g.ProtokolId is { } pid ? Ozet.Protokoller.GetValueOrDefault(pid) : null,
                g.KayitIdleri.Select(kayitlar.GetValueOrDefault).OfType<SyncLogEntry>().ToList())
            { HastaGrubu = g.HastaGrubu })
            .Where(g => g.Kayitlar.Count > 0)
            .ToList();

        await AciklamalariDoldurAsync(Entries, ct);
    }

    // ------------------------------------------------------------------ ARALIK OZETI
    //
    // Onbellek anahtari TUM filtreleri tasiyor -- sayfa numarasi HARIC. Ozet zaten
    // araligin tamami; sayfa numarasini anahtara koymak her sayfa icin ayni hesabi
    // yeniden yaptirirdi ve onbellegin varlik sebebini ortadan kaldirirdi.
    private async Task<AkisOzeti> OzetAlAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var anahtar = $"aktivite-ozet|{fromUtc:O}|{toUtcExclusive:O}|{Status}|{ResourceType}|{Kategori}";
        return await cache.GetOrCreateAsync(anahtar, async giris =>
        {
            giris.AbsoluteExpirationRelativeToNow = OnbellekOmru;
            return await OzetiHesaplaAsync(fromUtc, toUtcExclusive, ct);
        }) ?? AkisOzeti.Bos;
    }

    private async Task<AkisOzeti> OzetiHesaplaAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        // Message yalnizca kategori filtresi acikken gerekiyor (kategori mesaj metninden
        // turetiliyor, DB kolonu degil).
        var kategoriVar = Status == "Failed" && !string.IsNullOrWhiteSpace(Kategori);
        var satirlar = await syncLog.QueryOzetAsync(
            Status, ResourceType, fromUtc, toUtcExclusive, OzetTavani, kategoriVar, ct);

        var kirpildi = satirlar.Count >= OzetTavani;
        if (kategoriVar)
            satirlar = satirlar.Where(k => SyncLogEntry.ErrorCategory(k.Message).Label == Kategori).ToList();

        if (satirlar.Count == 0) return AkisOzeti.Bos;

        // ---- 1) PROTOKOL COZUMU ----
        // SyncLog'da protokol bagi YOK -- her kaynak tipi kendi id uzayini kullaniyor.
        // Protokol numarasini Pusula'ya sorarak cozuyoruz (bkz. ProtokolIdleriniCozAsync).
        //
        // PUSULA ERISILEMEZSE SAYFA YINE ACILIR: bu sayfa bugune kadar HIC Pusula'ya
        // baglanmiyordu, yalnizca SyncLog okuyordu. Gruplama ugruna "Pusula yoksa senkron
        // gunlugu de yok" durumuna dusmek yanlis olurdu -- gunluk, tam da Pusula'ya
        // erisilemedigi anda en cok ihtiyac duyulan sey.
        var esleme = new Dictionary<(string, int), int>();
        var pusulaHatasi = false;
        foreach (var tip in satirlar.GroupBy(k => k.ResourceType))
        {
            try
            {
                var cozum = await repository.ProtokolIdleriniCozAsync(
                    tip.Key, tip.Select(k => k.PusulaId).Distinct().ToList(), ct);
                foreach (var kv in cozum) esleme[(tip.Key, kv.Key)] = kv.Value;
            }
            catch (Exception ex)
            {
                pusulaHatasi = true;
                logger.LogWarning(ex, "Aktivite akisi: {Tip} icin protokol numaralari cozulemedi.", tip.Key);
            }
        }

        // ---- 2) GRUPLARA AYIR ----
        // Uc kova: protokolu cozulenler, Patient kayitlari, geri kalan.
        var protokolKovalari = new Dictionary<int, List<SyncLogStore.OzetSatiri>>();
        var hastaKovalari = new Dictionary<int, List<SyncLogStore.OzetSatiri>>();
        var kimliksiz = new List<SyncLogStore.OzetSatiri>();

        foreach (var k in satirlar)
        {
            if (esleme.TryGetValue((k.ResourceType, k.PusulaId), out var pid))
                Kovala(protokolKovalari, pid, k);
            else if (k.ResourceType == "Patient")
                Kovala(hastaKovalari, k.PusulaId, k);
            else
                kimliksiz.Add(k);
        }

        // ---- 3) PROTOKOL BILGILERI ----
        var protokoller = new Dictionary<int, ProtokolListItem>();
        if (protokolKovalari.Count > 0)
        {
            try
            {
                protokoller = await repository.GetProtokollerByIdsAsync(protokolKovalari.Keys.ToList(), ct);
            }
            catch (Exception ex)
            {
                pusulaHatasi = true;
                logger.LogWarning(ex, "Aktivite akisi: protokol bilgileri okunamadi.");
            }
        }

        // ---- 4) PATIENT KAYITLARINI PROTOKOL GRUBUNA KAT ----
        //
        // NEDEN GEREKLI: Patient kaydinin PusulaId'si HastaId, protokol degil -- hasta
        // protokoller arasi paylasilir, bu yuzden ProtokolIdleriniCozAsync onu bilerek
        // cozmez. Eski halde bu kayitlarin HEPSI tek bir "protokolsuz" kovasina
        // dusuyordu. Oysa ProtocolFullSyncService her protokol gonderiminde bir Patient
        // satiri da yaziyor; yani bir gunun Patient satiri sayisi protokol sayisi
        // kadardir. Hepsini tek kovada toplamak hem listeyi hem de "kac hasta" sayisini
        // anlamsiz kilardi.
        //
        // AYNI HASTANIN ARALIKTA BIRDEN COK PROTOKOLU VARSA en yenisi seciliyor
        // (en buyuk SyncLog Id). Patient gonderimi protokol gonderiminin ICINDEN
        // tetiklendigi icin pratikte dogru olan da bu; kesin bag SyncLog'da yok.
        var hastaninProtokolu = new Dictionary<int, (int ProtokolId, long EnYeniId)>();
        foreach (var (pid, kova) in protokolKovalari)
        {
            if (!protokoller.TryGetValue(pid, out var pr) || pr.HastaId == 0) continue;
            var enYeni = kova.Max(k => k.Id);
            if (!hastaninProtokolu.TryGetValue(pr.HastaId, out var mevcut) || enYeni > mevcut.EnYeniId)
                hastaninProtokolu[pr.HastaId] = (pid, enYeni);
        }

        foreach (var (hastaId, kova) in hastaKovalari.ToList())
        {
            if (!hastaninProtokolu.TryGetValue(hastaId, out var hedef)) continue;
            protokolKovalari[hedef.ProtokolId].AddRange(kova);
            hastaKovalari.Remove(hastaId);
        }

        // ---- 5) SIRALA ----
        // En yeni kayit en ustte. Kimligi cozulemeyen kova (doktor kayitlari ve
        // cozulemeyen satirlar) her zaman EN SONDA: tek bir isim altinda toplanamayan
        // karisik bir kume, listenin ortasinda gorunmesi kafa karistirirdi.
        var gruplar = protokolKovalari
            .Select(kv => new AkisGrupOzeti(kv.Key, false, Siralanmis(kv.Value), Say(kv.Value, SyncStatus.Success),
                Say(kv.Value, SyncStatus.Skipped), Say(kv.Value, SyncStatus.Failed), kv.Value.Max(k => k.Id)))
            .Concat(hastaKovalari.Select(kv => new AkisGrupOzeti(null, true, Siralanmis(kv.Value),
                Say(kv.Value, SyncStatus.Success), Say(kv.Value, SyncStatus.Skipped),
                Say(kv.Value, SyncStatus.Failed), kv.Value.Max(k => k.Id))))
            .OrderByDescending(g => g.EnYeniKayitId)
            .ToList();

        if (kimliksiz.Count > 0)
            gruplar.Add(new AkisGrupOzeti(null, false, Siralanmis(kimliksiz), Say(kimliksiz, SyncStatus.Success),
                Say(kimliksiz, SyncStatus.Skipped), Say(kimliksiz, SyncStatus.Failed), kimliksiz.Max(k => k.Id)));

        // ---- 6) SAYIMLAR ----
        // HASTA SAYIMI "EN KOTU DURUM KAZANIR" KURALIYLA: bir hastanin 40 kaydindan
        // biri hata aldiysa o hasta hatali sayilir. Kullanici bu karta "hangi hastalarda
        // sorun var" diye tikliyor; "39 kaydi gectigi icin basarili" demek onu yanlis
        // yere gotururdu. Kategoriler bu sayede ortusmuyor, toplamlari hasta sayisini
        // veriyor.
        //
        // KIMLIKSIZ KOVA HASTA SAYILMIYOR: icinde doktor kayitlari ve hastasi
        // cozulemeyen satirlar var, tek bir kisiye karsilik gelmiyor. Listede duruyor
        // ama "kac hasta" sorusunun cevabina karismiyor.
        var hastaGruplari = gruplar.Where(g => g.ProtokolId is not null || g.HastaGrubu).ToList();

        return new AkisOzeti(
            gruplar, protokoller,
            ToplamKayit: satirlar.Count,
            BasariliKayit: satirlar.Count(k => k.Status == SyncStatus.Success),
            AtlananKayit: satirlar.Count(k => k.Status == SyncStatus.Skipped),
            HataliKayit: satirlar.Count(k => k.Status == SyncStatus.Failed),
            HastaToplam: hastaGruplari.Count,
            HastaBasarili: hastaGruplari.Count(g => g.Hatali == 0 && g.Atlanan == 0),
            HastaAtlanan: hastaGruplari.Count(g => g.Hatali == 0 && g.Atlanan > 0),
            HastaHatali: hastaGruplari.Count(g => g.Hatali > 0),
            Kirpildi: kirpildi,
            PusulaHatasi: pusulaHatasi);

        static void Kovala(Dictionary<int, List<SyncLogStore.OzetSatiri>> kovalar, int anahtar,
            SyncLogStore.OzetSatiri satir)
        {
            if (!kovalar.TryGetValue(anahtar, out var kova)) kovalar[anahtar] = kova = [];
            kova.Add(satir);
        }

        static List<long> Siralanmis(List<SyncLogStore.OzetSatiri> k)
            => k.OrderByDescending(x => x.Id).Select(x => x.Id).ToList();

        static int Say(List<SyncLogStore.OzetSatiri> k, SyncStatus d) => k.Count(x => x.Status == d);
    }

    // Gonderilen kaydin insan okunur adi -- protokol cozumuyle ayni dayaniklilikla
    // (Pusula erisilemezse sutun bos kalir, sayfa yine acilir).
    private async Task AciklamalariDoldurAsync(List<SyncLogEntry> kayitlar, CancellationToken ct)
    {
        foreach (var tip in kayitlar.GroupBy(k => k.ResourceType))
        {
            if (!PusulaRepository.AciklamaDestekleniyorMu(tip.Key)) continue;
            try
            {
                var adlar = await repository.KayitAciklamalariAsync(
                    tip.Key, tip.Select(k => k.PusulaId).Distinct().ToList(), ct);
                foreach (var kv in adlar) Aciklamalar[(tip.Key, kv.Key)] = kv.Value;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Aktivite akisi: {Tip} icin kayit adlari okunamadi.", tip.Key);
            }
        }
    }
}

// Tarih araliginin TAMAMININ ozeti -- sayfalar arasinda paylasilan, onbellege alinan
// hesap. Gruplar sirali; sayfalama bu listenin uzerinde yapiliyor.
public record AkisOzeti(
    List<AkisGrupOzeti> Gruplar,
    Dictionary<int, ProtokolListItem> Protokoller,
    int ToplamKayit, int BasariliKayit, int AtlananKayit, int HataliKayit,
    int HastaToplam, int HastaBasarili, int HastaAtlanan, int HastaHatali,
    bool Kirpildi, bool PusulaHatasi)
{
    public static readonly AkisOzeti Bos = new([], new(), 0, 0, 0, 0, 0, 0, 0, 0, false, false);
}

// Tek bir grubun (hasta/protokol) ozeti: hangi SyncLog satirlari, hangi sayilar.
// Govdeler burada YOK -- yalnizca ekrana dusen gruplar icin ayrica okunuyor.
public record AkisGrupOzeti(int? ProtokolId, bool HastaGrubu, List<long> KayitIdleri,
    int Basarili, int Atlanan, int Hatali, long EnYeniKayitId);

// Aktivite akisinda tek bir grubun o sayfadaki kayitlari.
public record AktiviteGrubu(int? ProtokolId, ProtokolListItem? Protokol, List<SyncLogEntry> Kayitlar)
{
    // Protokolu olmayan ama HASTASI belli grup: aralikta protokol gonderimi olmayan
    // bir Patient kaydi (bkz. AktiviteModel, 4. adim). Kimligi SyncLog'un kendi
    // alanlarindan geliyor, Pusula'ya sorulmasi gerekmiyor.
    public bool HastaGrubu { get; init; }

    // Ne protokolu ne hastasi belli: doktor kayitlari ve protokolu cozulemeyen satirlar.
    public bool Kimliksiz => ProtokolId is null && !HastaGrubu;

    public int Basarili => Kayitlar.Count(k => k.Status == SyncStatus.Success);
    public int Hatali => Kayitlar.Count(k => k.Status == SyncStatus.Failed);
    public int Atlanan => Kayitlar.Count(k => k.Status == SyncStatus.Skipped);

    // Grup basligindaki saat -- gruptaki EN YENI kayit (akis en yeniden eskiye okunuyor).
    public DateTime SonZamanYerel => AzTime.ToLocal(Kayitlar.Max(k => k.CreatedAtUtc));

    // Protokol bilgisi okunamadiysa bile kayitlardaki hasta adi gosterilebilir.
    //
    // KIMLIKSIZ GRUPTA ISIM YOK (2026-10-06 duzeltmesi). Ilk halde bu grup da ilk
    // kaydin hasta adini basliga koyuyordu -- ama o grup FARKLI hastalarin ve doktorlarin
    // kayitlarini bir arada tutuyor, dolayisiyla tek bir isim duz yanlis bilgi oluyordu.
    // Kullanici "NƏRGIZ ALIYEVA" basligi altinda uc ayri hastanin satirini gorup ayni
    // hastanin uc kez gonderildigini dusundu; hakliydi, ekran oyle soyluyordu.
    // Kimlik artik satir duzeyinde: her satirin kendi adi "Gonderim icerigi" sutununda.
    public string? HastaAdi => Kimliksiz
        ? null
        : Protokol?.HastaAdiSoyadi is { Length: > 0 } ad
            ? ad
            : Kayitlar.Select(k => k.PatientFullName).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

    public string? Fin => Kimliksiz
        ? null
        : Protokol?.Fin is { Length: > 0 } f
            ? f
            : Kayitlar.Select(k => k.Fin).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
}
