using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

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
    IMemoryCache cache, TekilGonderimService tekilGonderim, DeleteService deleteService,
    ProtocolFullSyncService protocolFullSync, CancellationSyncService cancellationSync,
    SettingsStore settings, ILogger<AktiviteModel> logger) : PageModel
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

    // Secili suzgecten SONRA listelenen grup sayisi. Ust karttaki sayilardan FARKLI
    // olabilir: kategori suzgeci devredeyken "Hatali" 7 hasta gosterirken liste o
    // kategoriye uyan 2 hastayi listeler.
    public int ListelenenGrupSayisi { get; set; }

    // Sayfalama bilgisi ("... hastanin 26-50 arasi gosteriliyor"). Birim GRUP, satir degil.
    public int IlkSira => (PageNumber - 1) * PageSize + 1;
    public int SonSira => IlkSira + Gruplar.Count - 1;

    // Ust kart sayilari -- ARALIGIN TAMAMI, sayfadan bagimsiz.
    public AkisOzeti Ozet { get; set; } = AkisOzeti.Bos;

    // Gonderim/silme sonucu. TempData: islemler POST-REDIRECT-GET ile bitiyor (tazeleme
    // ayni gonderimi tekrar yapmasin diye), mesajin yonlendirmeden sagligiyla cikmasi lazim.
    [TempData] public string? IslemMesaji { get; set; }
    [TempData] public bool IslemBasarili { get; set; }

    // SAYFA BOYUTU ARTIK GRUP SAYISI (hasta/protokol), satir sayisi degil. 25 grup, bir
    // ekranda kapali halde rahat sigiyor; acildiginda satir sayisi gruba gore degisiyor
    // ama bu kullanicinin zaten gormek istedigi derinlik.
    private const int PageSize = 25;

    // Aralikta okunacak azami KAYIT -- bellek sigortasi.
    //
    // 2026-10-09'A KADAR SATIR TAVANIYDI VE EKRANI YALAN SOYLETIYORDU: tavan asildiginda
    // sorgu "en yeni 60.000 SATIR" donuyordu, yani baslangic tarihi fiilen yok sayiliyordu.
    // Kullanici 01.10'dan baslatip 84 hasta, 03.10'dan baslatip 451 hasta gordu ve hakli
    // olarak "daha az gunde nasil cok data olur" diye sordu -- iki sorgu da ayni 60.000
    // satiri okuyordu, kayit sayilari bile birebir aynisi cikiyordu.
    //
    // Birim artik KAYIT: olculdu, 239.442 satir = 23.228 farkli kayit (10,3 kat). Ayni
    // sayiyla on kat genis aralik kapsaniyor, pratikte tavan hic devreye girmiyor.
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

    // ARAMA (2026-10-21, "D" tasarimi): hasta adi, FIN ya da protokol numarasi. Ozetin
    // uzerinde, bellekte -- protokol bilgileri ozet hesabinda zaten okunmus durumda,
    // aramak icin Pusula'ya yeniden gidilmiyor. Kartlari etkilemiyor, yalnizca listeyi.
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    // SIRALAMA: "son" (en son denenen, varsayilan) | "deneme" (en cok deneme) |
    // "tarih" (protokol acilis tarihi, yeniden eskiye).
    [BindProperty(SupportsGet = true)]
    public string? Sira { get; set; }

    // Canli gonderim seridi -- ONBELLEGE ALINMIYOR: "su an ne oluyor" sorusunun
    // cevabi iki dakika bayat olamaz. Ayar okumalari ucuz (bkz. SettingsStore).
    public CanliSerit Serit { get; set; } = CanliSerit.Bos;

    // Tekil gonderimde kaydin hangi protokole ait oldugu -- formdan gelir (akis protokole
    // gore grupli, yani ekran bunu zaten biliyor). Tahmin edilmez.
    [BindProperty]
    public int? ProtokolId { get; set; }

    // Genel Bakış'taki "Hata Kategorileri" kutucuklarından geliyor -- SyncLogEntry.ErrorCategory
    // ile ayni etiketle eslesen Failed kayitlarini gosterir. Bu bir DB sutunu degil (mesaj
    // metninden turetiliyor), o yuzden SQL'de degil, bellek icinde filtreleniyor.
    [BindProperty(SupportsGet = true)]
    public string? Kategori { get; set; }

    // "Hic gonderilemeyen" karti Status'e bu degeri koyuyor. SyncStatus adlariyla
    // (Success/Skipped/Failed) cakismiyor; Enum.TryParse basarisiz olur ve durum
    // suzgeci devre disi kalir -- tam istenen davranis (bkz. OnGetAsync).
    public const string HicbiriAnahtari = "HicGitmedi";

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

        // OZET FILTRESIZ: ust karttaki hasta sayilari her zaman araligin TAMAMINI
        // anlatiyor, hangi kartin secili oldugundan bagimsiz (bkz. QueryOzetAsync).
        Ozet = await OzetAlAsync(fromUtc, toUtcExclusive, ct);

        // ---- SUZME: KARTLARI DEGIL, LISTEYI DARALTIR ----
        //
        // KULLANICI (2026-10-07): "sorunsuza tiklayinca toplam hasta 71 oluyor, eksik
        // veriye tiklayinca sorunsuz 0 oluyor -- bir tuhaflik var."
        //
        // Hakliydi. Suzgec SQL'e kadar iniyordu: "Sorunsuz"a basilinca yalnizca basarili
        // satirlar okunuyor, hastalarin kalan kayitlari hic gorulmedigi icin herkes
        // sorunsuz cikiyor ve kart kendi sayisini yeniden yaziyordu. Yani kartlar, kendi
        // tiklanmalarinin sonucunu olcuyorlardi.
        //
        // Artik secili kart, hastayi ARALIGIN TAMAMINA gore aldigi sinifla esliyor:
        // "Hatali"ya basinca ustte yazan 7 hastanin ta kendisi listeleniyor. Grup icinde
        // ise yalnizca o durumdaki satirlar gosteriliyor -- 200 kaydi olan bir hastada
        // hata alan 3 satiri aramak zorunda kalinmasin diye.
        // "HIC GONDERILEMEYEN" AYRI BIR SUZGEC (2026-10-10, kullanici istegi:
        // "aktivite ekranina hicbir gonderimi yapilamayan protokol sayilarini yazan bir
        // kutu mu eklesek").
        //
        // Bu bir SyncStatus DEGIL: diger dort kart birbirini disliyor ve toplamlari
        // Toplam'a esit. Bu olcu onlarin UZERINE biniyor -- "Eksik verili" ve "Hatali"
        // hastalarin bir ALT KUMESI (hic basarili kaydi olmayanlar). Ayri bir anahtar
        // kelimeyle tasinmasinin sebebi bu; Status'e karistirilsaydi dort kartin
        // toplami bozulurdu.
        var hicbiriSuzgeci = Status == HicbiriAnahtari;
        var seciliDurum = hicbiriSuzgeci
            ? null
            : Enum.TryParse<SyncStatus>(Status, out var d) ? d : (SyncStatus?)null;
        var kategoriSuzgeci = seciliDurum == SyncStatus.Failed && !string.IsNullOrWhiteSpace(Kategori)
            ? Kategori : null;

        Serit = await SeritOkuAsync(ct);

        var aranan = Normalize(Q);
        var listelenen = Ozet.Gruplar
            .Where(g => !hicbiriSuzgeci || g.HicGitmedi)
            .Where(g => seciliDurum is null || g.Sinif == seciliDurum)
            .Where(g => kategoriSuzgeci is null || g.Kayitlar.Any(r => r.HataKategorisi == kategoriSuzgeci))
            .Where(g => aranan is null || AramayaUyar(g, aranan))
            .ToList();
        listelenen = Sirala(listelenen);
        ListelenenGrupSayisi = listelenen.Count;

        var sayfaGruplari = listelenen.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        HasNextPage = listelenen.Count > PageNumber * PageSize;

        // Grup icinde gosterilecek satirlar. Govdeler YALNIZCA bunlar icin okunuyor
        // (bkz. SyncLogStore.GetByIdsAsync).
        // Sozluk DEGIL liste: AkisGrupOzeti bir record ve icinde List tasiyor, sozluk
        // anahtari yapilsaydi her aramada yedi alan uzerinden hash hesaplanirdi. Grup ile
        // satirlarini yan yana tutmak hem ucuz hem acik.
        var sayfaSatirlari = sayfaGruplari
            .Select(g => (Grup: g, Kimlikler: g.Kayitlar
                .Where(r => seciliDurum is null || r.Durum == seciliDurum)
                .Where(r => kategoriSuzgeci is null || r.HataKategorisi == kategoriSuzgeci)
                .Select(r => (r.ResourceType, r.PusulaId)).Distinct().ToList()))
            .ToList();

        // DENEMELER BURADA OKUNUYOR. Ozet kayit basina tek satir tasiyor; tablonun
        // gosterdigi tek tek denemeler yalnizca bu sayfadaki gruplar icin getiriliyor.
        Entries = await syncLog.GetByRecordsAsync(
            sayfaSatirlari.SelectMany(x => x.Kimlikler).ToList(), fromUtc, toUtcExclusive, ct);

        var denemeler = Entries.ToLookup(e => (e.ResourceType, e.PusulaId));
        Gruplar = sayfaSatirlari
            .Select(x => new AktiviteGrubu(
                x.Grup,
                x.Grup.ProtokolId is { } pid ? Ozet.Protokoller.GetValueOrDefault(pid) : null,
                x.Kimlikler.SelectMany(k => denemeler[k]).OrderByDescending(e => e.Id).ToList()))
            .Where(g => g.Kayitlar.Count > 0)
            .ToList();

        await AciklamalariDoldurAsync(Entries, ct);
    }

    // ------------------------------------------------------------------ ARALIK OZETI
    //
    // Onbellek anahtarinda YALNIZCA tarih araligi ve kayit turu var. Durum ve kategori
    // filtreleri ozeti degistirmiyor (ikisi de ekrana hangi gruplarin alinacagi
    // secilirken uygulaniyor), dolayisiyla kartlar arasinda gezinmek ayni ozeti
    // kullaniyor -- hesap yeniden yapilmiyor. Sayfa numarasi da anahtarda yok: ozet
    // zaten araligin tamami.
    private async Task<AkisOzeti> OzetAlAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var anahtar = $"aktivite-ozet|{fromUtc:O}|{toUtcExclusive:O}|{ResourceType}";
        return await cache.GetOrCreateAsync(anahtar, async giris =>
        {
            var ozet = await OzetiHesaplaAsync(fromUtc, toUtcExclusive, ct);

            // PUSULA PATLADIYSA ONBELLEGE ALMA (2026-10-09).
            //
            // Protokol cozumu basarisiz oldugunda hasta sayisi YANLIS cikiyor ve iki
            // dakika boyunca o yanlis sayi herkese gosteriliyordu. Kullanici tam bunu
            // gordu: ayni veri icin bir seferinde 340, digerinde 650 hasta. Gecici bir
            // ag kesintisinin sonucunu onbellege almak, hatayi kalicilastirmak demek.
            // Sifir omurle yazinca bir sonraki acilis yeniden deniyor.
            giris.AbsoluteExpirationRelativeToNow =
                ozet.PusulaHatasi ? TimeSpan.FromTicks(1) : OnbellekOmru;
            return ozet;
        }) ?? AkisOzeti.Bos;
    }

    private async Task<AkisOzeti> OzetiHesaplaAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var satirlar = await syncLog.QueryOzetAsync(
            ResourceType, fromUtc, toUtcExclusive, OzetTavani, ct);

        // KAYIT SAYILARI AYRI VE KIRPILMASIZ: ust karttaki "N kayit" alt satirlari
        // dogrudan SQL toplamindan geliyor, ozetin tavanindan etkilenmiyor. Eskiden
        // ikisi de ayni kirpilmis listeden hesaplandigi icin tavan asildiginda iki
        // farkli tarih araligi ayni kayit sayisini gosteriyordu.
        var durumSayilari = await syncLog.GetStatusCountsAsync(
            ResourceType, fromUtc, toUtcExclusive, ct);

        // KART SAYILARI AYRI VE TAVANSIZ -- ozet listesinden bagimsiz.
        var kartlar = await syncLog.AkisKartlariniHesaplaAsync(
            ResourceType, fromUtc, toUtcExclusive, ct);

        // Protokol bagi henuz doldurulmamis satir sayisi -- AYARDAN OKUNUYOR, SAYILMIYOR.
        //
        // 2026-10-21 DUZELTMESI: burasi eskiden ProtokolBagiEksikSayisiAsync cagiriyordu.
        // O sorgunun TARIH FILTRESI YOK; her sayfa acilisinda milyonlarca satirlik tablonun
        // tamamini tariyordu -- hem de yalnizca bir uyari satirindaki sayi icin. Sayfa
        // gozle gorulur sekilde yavasladi (kullanici bildirdi: "cok uzun suredir beni
        // bekletiyor").
        //
        // Degeri doldurma isi zaten her turda hesapliyor; buradan okumak O(1).
        // Birkac dakika bayat olabilir, ki bir ilerleme gostergesi icin fazlasiyla yeterli.
        var bagiEksik = await settings.GetIntAsync(SettingsStore.ProtokolBagiKalanKey, 0, ct);

        var kirpildi = satirlar.Count >= OzetTavani;
        if (satirlar.Count == 0) return AkisOzeti.Bos;

        // ---- 1) PROTOKOL COZUMU ----
        // ONCE SyncLog.ProtokolId (2026-10-21): bag yazma aninda kaydediliyor, eski satirlar
        // da ProtokolBagiDoldurmaService ile dolduruluyor. Kartlar da bu sutundan sayiyor --
        // listenin ayni kaynaktan gruplanmasi, kartta yazan protokolun listede de ayni
        // protokol olmasinin tek garantisi.
        //
        // Pusula'ya YALNIZCA bagi henuz doldurulmamis (NULL) satirlar soruluyor. Doldurma
        // bitince bu adim pratikte hic calismaz. -1 "soruldu, bulunamadi" demek: o satirlar
        // yeniden sorulmaz, dogrudan kimliksiz kovaya duser.
        //
        // PUSULA ERISILEMEZSE SAYFA YINE ACILIR: gruplama ugruna "Pusula yoksa senkron
        // gunlugu de yok" durumuna dusmek yanlis olurdu -- gunluk, tam da Pusula'ya
        // erisilemedigi anda en cok ihtiyac duyulan sey.
        var esleme = new Dictionary<(string, int), int>();
        foreach (var k in satirlar)
            if (k.ProtokolId is int bag and > 0) esleme[(k.ResourceType, k.PusulaId)] = bag;

        var pusulaHatasi = false;
        foreach (var tip in satirlar
                     .Where(k => k.ProtokolId is null && k.ResourceType is not ("Patient" or "Practitioner"))
                     .GroupBy(k => k.ResourceType))
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
            var enYeni = kova.Max(k => k.SonId);
            if (!hastaninProtokolu.TryGetValue(pr.HastaId, out var mevcut) || enYeni > mevcut.EnYeniId)
                hastaninProtokolu[pr.HastaId] = (pid, enYeni);
        }

        foreach (var (hastaId, kova) in hastaKovalari.ToList())
        {
            if (!hastaninProtokolu.TryGetValue(hastaId, out var hedef)) continue;
            protokolKovalari[hedef.ProtokolId].AddRange(kova);
            hastaKovalari.Remove(hastaId);
        }

        // PUSULA OKUNAMADIYSA HASTA KAYITLARI AYRI GRUP OLMAZ (2026-10-09 duzeltmesi).
        //
        // Birlestirme HastaId'ye ihtiyac duyuyor, o da protokol bilgisinden geliyor.
        // Bilgi okunamazsa her Patient kaydi kendi grubu oluyor ve HASTA SAYISI YUKARI
        // SISIYOR. Kullanici tam bunu gordu: ayni veride bir seferinde 340, digerinde
        // 650 hasta -- aradaki 310 fark, o araliktaki 303 farkli Patient kaydi.
        //
        // Yukari sisen bir sayi, eksik bir sayidan daha kotu: "iyiye gidiyoruz" diye
        // okunuyor. Bilmiyorsak sayiya katmiyoruz -- kayitlar kimligi cozulemeyen
        // kovaya gidiyor ve ekran bunu zaten uyari olarak soyluyor.
        if (pusulaHatasi && hastaKovalari.Count > 0)
        {
            foreach (var kova in hastaKovalari.Values) kimliksiz.AddRange(kova);
            hastaKovalari.Clear();
        }

        // ---- 5) SIRALA ----
        // En yeni kayit en ustte. Kimligi cozulemeyen kova (doktor kayitlari ve
        // cozulemeyen satirlar) her zaman EN SONDA: tek bir isim altinda toplanamayan
        // karisik bir kume, listenin ortasinda gorunmesi kafa karistirirdi.
        var gruplar = protokolKovalari.Select(kv => Grup(kv.Key, false, kv.Value))
            .Concat(hastaKovalari.Select(kv => Grup(null, true, kv.Value)))
            .OrderByDescending(g => g.EnYeniKayitId)
            .ToList();

        if (kimliksiz.Count > 0) gruplar.Add(Grup(null, false, kimliksiz));

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
            // Kirpilmasiz SQL toplami -- tarih araligini gercekten yansitir.
            ToplamKayit: durumSayilari.Values.Sum(),
            BasariliKayit: durumSayilari.GetValueOrDefault(nameof(SyncStatus.Success)),
            AtlananKayit: durumSayilari.GetValueOrDefault(nameof(SyncStatus.Skipped)),
            HataliKayit: durumSayilari.GetValueOrDefault(nameof(SyncStatus.Failed)),
            // KART SAYILARI ARTIK TAVANSIZ SQL'DEN (2026-10-16). Eskiden bunlar da
            // kirpilmis listeden (hastaGruplari) sayiliyordu; kirpma devreye girdiginde
            // tarih araligi GENISLETILINCE sayi DUSEBILIYORDU -- kullanici bildirdi.
            // Ayrintili gerekce SyncLogStore.AkisKartlariniHesaplaAsync'te.
            //
            // Liste (gruplar) hala kirpili: o bir SAYFALANMIS GORUNUM, sayi degil.
            // Kirpma orada zararsiz; kart sayilarina artik hic karismiyor.
            HastaToplam: kartlar.Toplam,
            HastaBasarili: kartlar.Basarili,
            HastaAtlanan: kartlar.Atlanan,
            HastaHatali: kartlar.Hatali,
            HastaHicGitmediSql: kartlar.HicGitmedi,
            BagiEksikSatir: bagiEksik,
            Kirpildi: kirpildi,
            PusulaHatasi: pusulaHatasi);

        static void Kovala(Dictionary<int, List<SyncLogStore.OzetSatiri>> kovalar, int anahtar,
            SyncLogStore.OzetSatiri satir)
        {
            if (!kovalar.TryGetValue(anahtar, out var kova)) kovalar[anahtar] = kova = [];
            kova.Add(satir);
        }

        // Grup ozeti. Satirlar en yeniden eskiye; her satirin durumu VE (hatali ise)
        // hata kategorisi birlikte tasiniyor -- suzme artik SQL'de degil burada
        // yapildigi icin ikisine de sonradan ihtiyac var.
        //
        // AYNI KAYIT IKI SATIR GELEBILIR: sorgu ProtokolId ile de grupladigi icin, bagi
        // doldurulmamis eski denemeler (NULL) ile yeni denemeler (dolu) ayri satir doner.
        // Burada tek kayda katlaniyor -- durum ve mesaj SON denemeden, deneme sayisi toplam.
        // Katlanmasaydi kayit grupta iki kez sayilir, denemeleri de tabloda iki kez cikardi.
        static AkisGrupOzeti Grup(int? protokolId, bool hastaGrubu, List<SyncLogStore.OzetSatiri> k)
        {
            var kayitlar = k.GroupBy(x => (x.ResourceType, x.PusulaId))
                .Select(g =>
                {
                    var son = g.MaxBy(x => x.SonId)!;
                    return (Son: son, Deneme: g.Sum(x => x.DenemeSayisi));
                })
                .OrderByDescending(x => x.Son.SonId)
                .Select(x => new AkisKayitRef(x.Son.ResourceType, x.Son.PusulaId, x.Son.Status, x.Deneme,
                    x.Son.Status == SyncStatus.Failed ? SyncLogEntry.ErrorCategory(x.Son.HataMesaji).Label : null,
                    x.Son.HataMesaji))
                .ToList();
            return new AkisGrupOzeti(protokolId, hastaGrubu, kayitlar,
                kayitlar.Count(x => x.Durum == SyncStatus.Success),
                kayitlar.Count(x => x.Durum == SyncStatus.Skipped),
                kayitlar.Count(x => x.Durum == SyncStatus.Failed),
                k.Max(x => x.SonId));
        }
    }

    // ------------------------------------------------------------------ ARAMA / SIRALAMA

    private static readonly System.Globalization.CultureInfo Tr =
        System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    // tr-TR buyuk harfe cevirerek: "i" -> "İ", "ı" -> "I". Invariant ile yapilsaydi
    // "irade" araması "İRADƏ" adini bulamazdi.
    private static string? Normalize(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpper(Tr);

    private bool AramayaUyar(AkisGrupOzeti g, string aranan)
    {
        if (g.ProtokolId is not { } pid) return false;
        if (pid.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(aranan)) return true;
        if (!Ozet.Protokoller.TryGetValue(pid, out var pr)) return false;
        return (pr.Fin?.ToUpper(Tr).Contains(aranan) ?? false)
            || (pr.HastaAdiSoyadi?.ToUpper(Tr).Contains(aranan) ?? false);
    }

    private List<AkisGrupOzeti> Sirala(List<AkisGrupOzeti> gruplar) => Sira switch
    {
        "deneme" => gruplar.OrderByDescending(g => g.Kayitlar.Sum(k => k.DenemeSayisi)).ToList(),
        // Protokol bilgisi okunamayan gruplar (tarih yok) en sonda.
        "tarih" => gruplar
            .OrderByDescending(g => g.ProtokolId is { } pid && Ozet.Protokoller.TryGetValue(pid, out var pr)
                ? pr.AcilisTarihi ?? DateTime.MinValue : DateTime.MinValue)
            .ToList(),
        // Varsayilan zaten en son denenen (ozet bu sirayla geliyor).
        _ => gruplar,
    };

    // ------------------------------------------------------------------ CANLI SERIT
    //
    // Yalnizca AYAR okuyor -- dongunun kendisine dokunmuyor. Degerleri AutoSyncWorker
    // yaziyor (bkz. SettingsStore.AutoSendTurHedefKey). Sayfa acikken serit 15 saniyede
    // bir kendini tazeliyor (OnGetSeritAsync), sayfanin geri kalani degil.
    private async Task<CanliSerit> SeritOkuAsync(CancellationToken ct)
    {
        static DateTime? Zaman(string s) => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

        return new CanliSerit(
            Acik: await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct),
            TurNo: await settings.GetIntAsync(SettingsStore.AutoSendTurNoKey, 0, ct),
            Hedef: await settings.GetIntAsync(SettingsStore.AutoSendTurHedefKey, 0, ct),
            Islenen: await settings.GetIntAsync(SettingsStore.AutoSendTurIslenenKey, 0, ct),
            Bekleyen: await settings.GetIntAsync(SettingsStore.AutoSendTurBekleyenKey, 0, ct),
            SonrakiTurUtc: Zaman(await settings.GetStringAsync(SettingsStore.AutoSendNextRunUtcKey, "", ct)),
            SonTurUtc: Zaman(await settings.GetStringAsync(SettingsStore.AutoSendLastRunUtcKey, "", ct)),
            SonTurOzet: await settings.GetStringAsync(SettingsStore.AutoSendLastRunOzetKey, "", ct),
            Canli: await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey,
                SettingsStore.EHealthEnvironmentDefault, ct) == "Live",
            NabizUtc: Zaman(await settings.GetStringAsync(SettingsStore.AutoSendNabizUtcKey, "", ct)),
            AralikDakika: await settings.GetIntAsync(SettingsStore.AutoSendIntervalMinutesKey,
                SettingsStore.AutoSendIntervalMinutesDefault, ct));
    }

    // Seridin kendini tazelemesi: yalnizca serit parcasini dondurur.
    public async Task<IActionResult> OnGetSeritAsync(CancellationToken ct) =>
        Partial("_AktiviteSerit", await SeritOkuAsync(ct));

    // DURDUR -- Ayarlar'daki Durdur ile AYNI anahtar, ayni etki: dongu bir sonraki
    // protokolden once durur (bkz. AutoSyncWorker, tur ici kontrol).
    //
    // BASLAT BURADA YOK, BILEREK: baslatmak canli sisteme yazmaya baslamak demek ve
    // Ayarlar'daki dugme ortami ve parti boyutunu yazan bir onayla korunuyor. Durdurmak
    // ise her zaman guvenli -- en kotu ihtimalle gonderim bir tur gecikir.
    public async Task<IActionResult> OnPostDurdurAsync(CancellationToken ct)
    {
        await settings.SetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
        logger.LogWarning("Otomatik gonderim DURDURULDU (Aktivite Akisi seridinden).");
        Sonuclandir(true, "Otomatik gönderim durduruldu. Süren tur, elindeki protokolü bitirince duracak.");
        return GeriDon();
    }

    // =========================== GONDERME VE SILME ===========================
    //
    // KULLANICI ISTEGI (2026-10-08): "aktivite akisinda gonderme ve silme
    // fonksiyonlarini da ekleyelim -- tum silme ve gonderme prosesleri olsun, tek tek,
    // toplu, hasta bazli, hepsi."
    //
    // DORT ISLEM, DORDU DE MEVCUT SERVISLERI CAGIRIYOR -- bu ekrana ozel hicbir gonderim
    // ya da silme mantigi YAZILMADI. Sebep bu projede olculmus bir hata: 2026-09-15'te
    // toplu gonderim kendi zincirini tasiyordu, EncounterSyncService'ten ayristi ve
    // epikriz/laboratuvar/patoloji toplu gonderimde HIC gitmedi. Ayni tuzaga iki kez
    // dusmemek icin:
    //   Tek kayit gonder -> TekilGonderimService      (Detay sayfasi da ayni servisi kullanir)
    //   Tek kayit sil    -> DeleteService             (patoloji zincirini de o cozer)
    //   Hastanin tumunu gonder -> ProtocolFullSyncService  (otomatik dongunun kullandigi yol)
    //   Hastanin tumunu sil    -> CancellationSyncService.DeleteProtocolChainAsync
    //
    // SILME SIRASI BURADA TEKRARLANMIYOR: FHIR hala referans verilen bir kaynagi silmeyi
    // HTTP 409 ile reddediyor, yani sira distan ice olmak zorunda (raporlar -> epikriz/
    // islem/tani -> Muayine). O kural CancellationSyncService'te, tek kopya halinde.
    //
    // HASTA (Patient) TOPLU SILMEDE SILINMEZ: ayni hasta baska protokollerde de kullaniliyor,
    // bir protokolden silmek digerlerini kirardi. Hasta satirinin kendi "Sil" dugmesi var.

    public async Task<IActionResult> OnPostGonderAsync(long kayitId, CancellationToken ct)
    {
        var kayit = await syncLog.GetByIdAsync(kayitId, ct);
        if (kayit is null) return NotFound();

        // Protokol baglami: Tani/Islem/Lab/Radyoloji/Patoloji gonderimi buna ihtiyac
        // duyuyor. Akis protokole gore grupli oldugu icin numara formdan geliyor --
        // tahmin edilmiyor.
        var sonuc = await tekilGonderim.GonderAsync(kayit, ProtokolId, ct);
        if (sonuc.Kayit is { } yeni)
            Sonuclandir(yeni.Status != SyncStatus.Failed,
                yeni.Status == SyncStatus.Failed
                    ? $"Gönderilemedi: {SyncLogEntry.FriendlyError(yeni.Message)}"
                    : $"{SyncLogEntry.ResourceTypeLabel(kayit.ResourceType)} gönderildi.");
        else
            Sonuclandir(false, sonuc.Hata);

        return GeriDon();
    }

    public async Task<IActionResult> OnPostSilAsync(long kayitId, CancellationToken ct)
    {
        var kayit = await syncLog.GetByIdAsync(kayitId, ct);
        if (kayit is null) return NotFound();

        // AzResourceId bos ise TRƏS'te silinecek bir sey yok ($validate edilmis kayit).
        // Dugme zaten gosterilmiyor ama istek elle de gelebilir.
        if (kayit.AzResourceId is null)
        {
            Sonuclandir(false, "Bu kaydın TRƏS'te karşılığı yok -- silinecek bir şey bulunmuyor.");
            return GeriDon();
        }

        var sonuc = await deleteService.DeleteAsync(kayit, ct);
        Sonuclandir(sonuc.Status != SyncStatus.Failed,
            sonuc.Status == SyncStatus.Failed
                ? $"Silinemedi: {SyncLogEntry.FriendlyError(sonuc.Message)}"
                : $"{SyncLogEntry.ResourceTypeLabel(kayit.ResourceType)} TRƏS'ten silindi.");
        return GeriDon();
    }

    public async Task<IActionResult> OnPostGrupGonderAsync(int protokolId, CancellationToken ct)
    {
        var protokol = await repository.GetProtokolByIdAsync(protokolId, ct);
        if (protokol is null)
        {
            Sonuclandir(false, "Protokol Pusula'da bulunamadı.");
            return GeriDon();
        }

        var sonuc = await protocolFullSync.SyncAllAsync(protokol, ct);
        Sonuclandir(sonuc.EncounterStatus != SyncStatus.Failed,
            $"Protokol {protokolId} yeniden gönderildi (müayinə: {sonuc.EncounterStatus}).");
        return GeriDon();
    }

    public async Task<IActionResult> OnPostGrupSilAsync(int protokolId, CancellationToken ct)
    {
        var sonuc = await cancellationSync.DeleteProtocolChainAsync(
            protokolId, $"Aktivite Akışı \"Tümünü Sil\" ({protokolId})", ct);
        Sonuclandir(sonuc.Err == 0,
            $"Protokol {protokolId}: {sonuc.Ok} kayıt TRƏS'ten silindi"
            + (sonuc.Err > 0 ? $", {sonuc.Err} tanesi silinemedi." : "."));
        return GeriDon();
    }

    // SECILENLERI GONDER (2026-10-21, "D" tasarimi) -- GrupGonder'in toplu hali, ayni
    // servisle (ProtocolFullSyncService). Ayri bir toplu gonderim yolu YAZILMADI: bu
    // projede toplu gonderimin kendi zincirini tasimasi bir kez epikriz/lab/patolojinin
    // hic gitmemesine yol acti (bkz. yukaridaki not).
    //
    // TAVAN BIR SAYFA (25): istek tarayici beklerken suruyor ve her protokol tam zincir
    // demek. Daha buyugu otomatik dongunun isi; bu dugme "su gordugum takilanlari simdi
    // dene" icin.
    public async Task<IActionResult> OnPostTopluGonderAsync(List<int> protokolIds, CancellationToken ct)
    {
        var hedefler = protokolIds.Distinct().Take(PageSize).ToList();
        if (hedefler.Count == 0)
        {
            Sonuclandir(false, "Gönderilecek protokol seçilmedi.");
            return GeriDon();
        }

        int basarili = 0, basarisiz = 0;
        var bulunamayan = new List<int>();
        foreach (var pid in hedefler)
        {
            var protokol = await repository.GetProtokolByIdAsync(pid, ct);
            if (protokol is null) { bulunamayan.Add(pid); continue; }
            try
            {
                var sonuc = await protocolFullSync.SyncAllAsync(protokol, ct);
                if (sonuc.EncounterStatus == SyncStatus.Failed) basarisiz++; else basarili++;
            }
            catch (Exception ex)
            {
                basarisiz++;
                logger.LogWarning(ex, "Aktivite toplu gonderim: protokol {Id} gonderilemedi.", pid);
            }
        }

        Sonuclandir(basarisiz == 0 && bulunamayan.Count == 0,
            $"{hedefler.Count} protokol yeniden gönderildi: {basarili} başarılı, {basarisiz} başarısız"
            + (bulunamayan.Count > 0 ? $", {bulunamayan.Count} tanesi Pusula'da bulunamadı." : ".")
            + " Ayrıntı satırın zincir rozetlerinde.");
        return GeriDon();
    }

    private void Sonuclandir(bool basarili, string? mesaj)
    {
        IslemBasarili = basarili;
        IslemMesaji = mesaj;
    }

    // POST-REDIRECT-GET: tazeleme ayni gonderimi/silmeyi tekrar yapmasin. Filtre, tarih
    // araligi ve sayfa numarasi KORUNUYOR -- kullanici her islemden sonra listenin basina
    // dusseydi 25 hastalik bir sayfada tek tek calismak imkansiz olurdu.
    //
    // PageNumber DEGIL P: PageNumber yalnizca OnGetAsync'te atanıyor, POST handler'larinda
    // 0 kalir. Yonlendirmede onu kullansaydik P=0 gider, OnGetAsync de 1'e kirpar ve her
    // islemden sonra kullanici sayfa 1'e duserdi -- tam da kacinmak istedigimiz sey.
    private IActionResult GeriDon() => RedirectToPage("/Aktivite", new
    {
        Status, ResourceType, Kategori, Q, Sira, P = P < 1 ? 1 : P,
        From = From?.ToString("yyyy-MM-dd"),
        To = To?.ToString("yyyy-MM-dd"),
    });

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
    int HastaHicGitmediSql, int BagiEksikSatir,
    bool Kirpildi, bool PusulaHatasi)
{
    public static readonly AkisOzeti Bos =
        new([], new(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false);

    // HIC GONDERILEMEYEN: tek bir kaydi bile basariyla gitmemis hastalar.
    //
    // Diger dort kartin ALT KUMESI, besinci bir kova DEGIL -- "Eksik verili" ve
    // "Hatali" hastalarin icinden, hic basarisi olmayanlar. Ayri durmasinin sebebi
    // soyledigi seyin kokten farkli olmasi: 40 kaydindan 39'u giden bir hasta ile
    // hicbiri gitmeyen bir hasta ayni kartta ayni agirlikta goruruyor, oysa ikincisi
    // bakanlik tarafinda HIC VAR OLMAYAN bir hasta demek.
    // SQL'den geliyor (tavansiz); liste kirpili oldugu icin Gruplar'dan sayilamaz.
    public int HastaHicGitmedi => HastaHicGitmediSql;
    public int HicGitmeyenKayit => Gruplar.Where(g => g.HicGitmedi).Sum(g => g.Kayitlar.Count);
}

// Ozetteki tek bir KAYIT (satir degil): kimligi, en kotu durumu, kac kez denendigi ve
// (hatali ise) hata kategorisi. Denemelerin kendisi burada YOK -- yalnizca ekrana dusen
// gruplar icin ayrica okunuyor (bkz. SyncLogStore.GetByRecordsAsync).
//
// SATIR DEGIL KAYIT: ayni kaydin onlarca denemesini ozette tasimak, tarih araligi genis
// oldugunda tavani doldurup baslangic tarihini etkisiz kiliyordu (bkz. OzetTavani).
// Mesaj: son denemenin mesaji, yalnizca basarisizsa -- takilan satirin altindaki sebep
// metni buradan uretiliyor.
public record AkisKayitRef(string ResourceType, int PusulaId, SyncStatus Durum,
                           int DenemeSayisi, string? HataKategorisi, string? Mesaj);

// Tek bir grubun (hasta/protokol) ozeti.
public record AkisGrupOzeti(int? ProtokolId, bool HastaGrubu, List<AkisKayitRef> Kayitlar,
    int Basarili, int Atlanan, int Hatali, long EnYeniKayitId)
{
    // SINIFLANDIRMAYA GIREN KAYITLAR -- kartlarla ayni kume (bkz.
    // SyncLogStore.AkisKartlariniHesaplaAsync). Kartlar protokolu siniflarken Hasta
    // kaydini saymiyor (ProtokolId'si NULL); liste sayarsa bir hastasi hata alan protokol
    // kartta "Sorunsuz", listede "Hatali" cikar ve karta tiklayan onu bulamaz.
    // Yalnizca Hasta kaydindan olusan grupta (HastaGrubu) siniflanacak baska sey yok.
    private IEnumerable<AkisKayitRef> Siniflanan => ProtokolId is null
        ? Kayitlar
        : Kayitlar.Where(k => k.ResourceType != "Patient");

    // Tek bir kaydi bile gitmemis mi? Kaydi olmayan grup "gitmedi" SAYILMAZ --
    // gonderilecek bir sey yoksa gonderilememis de denmez.
    public bool HicGitmedi => Siniflanan.Any() && Siniflanan.All(k => k.Durum != SyncStatus.Success);

    // PROTOKOLUN SINIFI, "EN KOTU KAYIT KAZANIR" KURALIYLA (kaydin kendi durumu ise son
    // denemeden). Ust karttaki sayilar da, bir karta tiklandiginda listelenecek
    // protokoller de bu tek kuraldan geliyor -- kartta yazan sayi ile listelenen protokol
    // sayisi birbirini tutuyor.
    public SyncStatus Sinif => Siniflanan.Any(k => k.Durum == SyncStatus.Failed) ? SyncStatus.Failed
        : Siniflanan.Any(k => k.Durum == SyncStatus.Skipped) ? SyncStatus.Skipped
        : SyncStatus.Success;

    // ---- GONDERIM ZINCIRI (2026-10-21, onaylanan "D" tasarimi) ----
    //
    // Satirin ayirt edici ozelligi: zincirin HANGI HALKASINDA takildigi tek bakista
    // gorunsun. Rozet sirasi gonderim sirasi -- hasta yazilmadan muayine, muayine
    // yazilmadan tani/islem gidemez; soldan ilk sorunlu halka cogu zaman asil sebep.
    //
    // Vital ve Patoloji YALNIZCA VARSA: cogu protokolde hic yoklar, her satirda bos rozet
    // olarak durmalari zinciri okunmaz kilardi. Digerleri bossa da gorunur ("bu protokolde
    // radyoloji yok" da bir bilgi).
    private static readonly (string Ad, string Baslik, string[] Turler, bool HepGoster)[] Halkalar =
    [
        ("Hasta",   "Hasta",                       ["Patient"],          true),
        ("Muayinə", "Müayinə",                     ["Encounter"],        true),
        ("Tanı",    "Tanı",                        ["Condition"],        true),
        ("İşlem",   "İşlem",                       ["Procedure"],        true),
        ("Lab",     "Laboratuvar tetkiki",         ["Observation"],      true),
        ("Vital",   "Vital bulgu",                 ["Observation-Vital"], false),
        ("Rad",     "Radyoloji raporu",            ["DiagnosticReport"], true),
        ("Epikriz", "Epikriz",                     ["Composition"],      true),
        ("Patoloji", "Patoloji raporu / belgesi / bulgusu",
            ["DiagnosticReport-Patoloji", "Composition-Patoloji", "Observation-Patoloji"], false),
    ];

    public List<ZincirHalkasi> Zincir()
    {
        var sonuc = new List<ZincirHalkasi>();
        foreach (var (ad, baslik, turler, hepGoster) in Halkalar)
        {
            var k = Kayitlar.Where(x => turler.Contains(x.ResourceType)).ToList();
            if (k.Count == 0 && !hepGoster) continue;
            var hatali = k.Count(x => x.Durum == SyncStatus.Failed);
            var eksik = k.Count(x => x.Durum == SyncStatus.Skipped);
            sonuc.Add(new ZincirHalkasi(ad, baslik, k.Count, hatali, eksik,
                hatali > 0 ? HalkaDurumu.Hatali
                : eksik > 0 ? HalkaDurumu.Eksik
                : k.Count > 0 ? HalkaDurumu.Tamam
                : HalkaDurumu.Yok));
        }
        return sonuc;
    }
}

public enum HalkaDurumu { Yok, Tamam, Eksik, Hatali }

public record ZincirHalkasi(string Ad, string Baslik, int Adet, int Hatali, int Eksik, HalkaDurumu Durum)
{
    public int Takilan => Hatali + Eksik;

    public string Css => Durum switch
    {
        HalkaDurumu.Tamam => "ok",
        HalkaDurumu.Eksik => "eksik",
        HalkaDurumu.Hatali => "hata",
        _ => "yok",
    };

    // Rozetin title'i -- rengi gormeyen (renk korlugu, ekran okuyucu) icin ayni bilgi metin olarak.
    public string Aciklama => Durum switch
    {
        HalkaDurumu.Yok => $"{Baslik}: bu protokolde yok",
        HalkaDurumu.Tamam => $"{Baslik}: {Adet} kayıt, hepsi gönderildi",
        _ => $"{Baslik}: {Adet} kayıttan {Takilan} tanesi takıldı"
             + (Hatali > 0 && Eksik > 0 ? $" ({Hatali} hatalı, {Eksik} eksik verili)" : ""),
    };
}

// Aktivite akisinda tek bir grubun o sayfada GOSTERILEN kayitlari.
//
// Ozet ile Kayitlar bilerek ayri: Ozet hastanin aralikta ne yaptiginin TAMAMI,
// Kayitlar ise secili suzgecten gecenler. Grup basligindaki rozetler Ozet'ten
// okunuyor -- "Hatali"ya tiklayan kullanici, o hastanin 198 kaydinin da basariyla
// gittigini gormeye devam ediyor; tabloda yalnizca aradigi 2 hatali satir duruyor.
public record AktiviteGrubu(AkisGrupOzeti Ozet, ProtokolListItem? Protokol, List<SyncLogEntry> Kayitlar)
{
    public int? ProtokolId => Ozet.ProtokolId;

    // Protokolu olmayan ama HASTASI belli grup: aralikta protokol gonderimi olmayan
    // bir Patient kaydi (bkz. AktiviteModel, 4. adim). Kimligi SyncLog'un kendi
    // alanlarindan geliyor, Pusula'ya sorulmasi gerekmiyor.
    public bool HastaGrubu => Ozet.HastaGrubu;

    // Ne protokolu ne hastasi belli: doktor kayitlari ve protokolu cozulemeyen satirlar.
    public bool Kimliksiz => ProtokolId is null && !HastaGrubu;

    // Rozetler ARALIGIN TAMAMINI gosteriyor, gosterilen satirlari degil.
    public int Basarili => Ozet.Basarili;
    public int Hatali => Ozet.Hatali;
    public int Atlanan => Ozet.Atlanan;

    // Grubun KAYIT sayisi (deneme degil) -- ust karttaki sayimla ayni birim.
    public int ToplamKayit => Ozet.Kayitlar.Count;

    // Tabloda gosterilen DENEME sayisi. Kayit sayisindan fazla olabilir: bir kayit
    // birden cok kez denenmis olabilir.
    public int ToplamDeneme => Ozet.Kayitlar.Sum(k => k.DenemeSayisi);

    public bool Suzulmus => Kayitlar.Count < ToplamDeneme;

    public List<ZincirHalkasi> Zincir { get; } = Ozet.Zincir();

    public (string Css, string Etiket) DurumGosterimi => Ozet.Sinif switch
    {
        SyncStatus.Failed => ("hata", "Hatalı"),
        SyncStatus.Skipped => ("eksik", "Eksik verili"),
        _ => ("ok", "Sorunsuz"),
    };

    // ---- TAKILMA SEBEBI -- satirin altinda, aramaya gerek kalmasin ----
    //
    // Baslik NEYIN takildigini sayar ("9 işlem, 1 epikriz takıldı"), metin NEDEN takildigini
    // soyler: takilan kayitlarin en sik iki mesaji, kullanici diline cevrilmis haliyle.
    // Hepsini yazmak satiri paragraf yapardi; ayrintinin tamami satir acilinca tabloda.
    //
    // HASTA TAKILDIYSA AYRI BASLIK: Hasta yazilamazsa zincirin geri kalani hic denenmez.
    // "1 hasta takıldı" bunu anlatmiyor -- bir protokolun neden bos gorundugunu soyleyen
    // sey "en basta durdu" bilgisi.
    public string? SebepBaslik
    {
        get
        {
            var takilan = Zincir.Where(h => h.Takilan > 0).ToList();
            if (takilan.Count == 0) return null;

            var hasta = Zincir.FirstOrDefault(h => h.Ad == "Hasta");
            if (hasta is { Takilan: > 0 } && Zincir.All(h => h.Ad == "Hasta" || h.Durum != HalkaDurumu.Tamam))
                return "Zincir en başta durdu";

            var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            return string.Join(", ", takilan.Select(h => $"{h.Takilan} {h.Ad.ToLower(tr)}")) + " takıldı";
        }
    }

    public string? SebepMetni
    {
        get
        {
            var sebepler = Ozet.Kayitlar
                .Where(k => k.Durum != SyncStatus.Success)
                .GroupBy(k => SyncLogEntry.FriendlyError(k.Mesaj))
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();
            if (sebepler.Count == 0) return null;
            var metin = string.Join(" · ", sebepler.Take(2));
            return sebepler.Count > 2 ? $"{metin} (+{sebepler.Count - 2} farklı sebep daha)" : metin;
        }
    }

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

// Canli gonderim seridinin anlik goruntusu (bkz. AktiviteModel.SeritOkuAsync).
public record CanliSerit(bool Acik, int TurNo, int Hedef, int Islenen, int Bekleyen,
    DateTime? SonrakiTurUtc, DateTime? SonTurUtc, string SonTurOzet, bool Canli,
    DateTime? NabizUtc, int AralikDakika)
{
    public static readonly CanliSerit Bos = new(false, 0, 0, 0, 0, null, null, "", false, null, 5);

    // SERVIS YANIT VERMIYOR MU? (2026-10-23) Dongu her tur basinda ve her protokolden sonra
    // nabiz yaziyor. Esik: tur araliginin 3 kati, en az 30 dk -- tek bir agir adim
    // (bekleyen is taramasi, iptal senkronu) birkac dakika surebiliyor, yanlis alarm olmasin.
    // Nabiz HIC yoksa da durgun: servis ya hic baslatilmadi ya da eski surum calisiyor.
    public bool ServisDurgun => NabizUtc is not { } n
        || DateTime.UtcNow - n > TimeSpan.FromMinutes(Math.Max(30, 3 * AralikDakika));

    // Hedef > 0 = bir tur SURUYOR. Dongu her cikista sifirliyor (bkz. AutoSyncWorker).
    public bool TurSuruyor => Hedef > 0;

    // Anahtar kapatildi ama tur elindeki protokolu henuz bitirmedi -- "Durdurdum ama
    // hala gonderiyor" sorusunun cevabi ekranda olsun.
    public bool Durduruluyor => !Acik && TurSuruyor;

    public double Yuzde => Hedef > 0 ? Math.Clamp(100.0 * Islenen / Hedef, 0, 100) : 0;

    // Son tur hata ile bittiyse dongu bunu "HATA: ..." onekiyle yaziyor.
    public bool SonTurHatali => SonTurOzet.StartsWith("HATA", StringComparison.Ordinal);
}
