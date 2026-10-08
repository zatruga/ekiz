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
    ILogger<AktiviteModel> logger) : PageModel
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

    // Tekil gonderimde kaydin hangi protokole ait oldugu -- formdan gelir (akis protokole
    // gore grupli, yani ekran bunu zaten biliyor). Tahmin edilmez.
    [BindProperty]
    public int? ProtokolId { get; set; }

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
        var seciliDurum = Enum.TryParse<SyncStatus>(Status, out var d) ? d : (SyncStatus?)null;
        var kategoriSuzgeci = seciliDurum == SyncStatus.Failed && !string.IsNullOrWhiteSpace(Kategori)
            ? Kategori : null;

        var listelenen = Ozet.Gruplar
            .Where(g => seciliDurum is null || g.Sinif == seciliDurum)
            .Where(g => kategoriSuzgeci is null || g.Kayitlar.Any(r => r.HataKategorisi == kategoriSuzgeci))
            .ToList();
        ListelenenGrupSayisi = listelenen.Count;

        var sayfaGruplari = listelenen.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        HasNextPage = listelenen.Count > PageNumber * PageSize;

        // Grup icinde gosterilecek satirlar. Govdeler YALNIZCA bunlar icin okunuyor
        // (bkz. SyncLogStore.GetByIdsAsync).
        // Sozluk DEGIL liste: AkisGrupOzeti bir record ve icinde List tasiyor, sozluk
        // anahtari yapilsaydi her aramada yedi alan uzerinden hash hesaplanirdi. Grup ile
        // satirlarini yan yana tutmak hem ucuz hem acik.
        var sayfaSatirlari = sayfaGruplari
            .Select(g => (Grup: g, Idler: g.Kayitlar
                .Where(r => seciliDurum is null || r.Durum == seciliDurum)
                .Where(r => kategoriSuzgeci is null || r.HataKategorisi == kategoriSuzgeci)
                .Select(r => r.Id).ToList()))
            .ToList();

        Entries = await syncLog.GetByIdsAsync(sayfaSatirlari.SelectMany(x => x.Idler).ToList(), ct);

        var kayitlar = Entries.ToDictionary(e => e.Id);
        Gruplar = sayfaSatirlari
            .Select(x => new AktiviteGrubu(
                x.Grup,
                x.Grup.ProtokolId is { } pid ? Ozet.Protokoller.GetValueOrDefault(pid) : null,
                x.Idler.Select(kayitlar.GetValueOrDefault).OfType<SyncLogEntry>().ToList()))
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
            giris.AbsoluteExpirationRelativeToNow = OnbellekOmru;
            return await OzetiHesaplaAsync(fromUtc, toUtcExclusive, ct);
        }) ?? AkisOzeti.Bos;
    }

    private async Task<AkisOzeti> OzetiHesaplaAsync(DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct)
    {
        var satirlar = await syncLog.QueryOzetAsync(
            ResourceType, fromUtc, toUtcExclusive, OzetTavani, ct);

        var kirpildi = satirlar.Count >= OzetTavani;
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

        // Grup ozeti. Satirlar en yeniden eskiye; her satirin durumu VE (hatali ise)
        // hata kategorisi birlikte tasiniyor -- suzme artik SQL'de degil burada
        // yapildigi icin ikisine de sonradan ihtiyac var.
        static AkisGrupOzeti Grup(int? protokolId, bool hastaGrubu, List<SyncLogStore.OzetSatiri> k)
        {
            var satirlar = k.OrderByDescending(x => x.Id)
                .Select(x => new AkisKayitRef(x.Id, x.Status,
                    x.Status == SyncStatus.Failed ? SyncLogEntry.ErrorCategory(x.Message).Label : null))
                .ToList();
            return new AkisGrupOzeti(protokolId, hastaGrubu, satirlar,
                k.Count(x => x.Status == SyncStatus.Success),
                k.Count(x => x.Status == SyncStatus.Skipped),
                k.Count(x => x.Status == SyncStatus.Failed),
                k.Max(x => x.Id));
        }
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
        Status, ResourceType, Kategori, P = P < 1 ? 1 : P,
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
    bool Kirpildi, bool PusulaHatasi)
{
    public static readonly AkisOzeti Bos = new([], new(), 0, 0, 0, 0, 0, 0, 0, 0, false, false);
}

// Ozetteki tek bir SyncLog satiri: yalnizca kimligi, durumu ve (hatali ise) hata
// kategorisi. Govde yok -- ekrana dusen satirlar icin ayrica okunuyor.
//
// HataKategorisi burada hazir duruyor cunku "Hata Kategorileri" suzgeci artik SQL'de
// degil bellekte uygulaniyor; mesaj metni ise ozetten sonra birakiliyor.
public record AkisKayitRef(long Id, SyncStatus Durum, string? HataKategorisi);

// Tek bir grubun (hasta/protokol) ozeti.
public record AkisGrupOzeti(int? ProtokolId, bool HastaGrubu, List<AkisKayitRef> Kayitlar,
    int Basarili, int Atlanan, int Hatali, long EnYeniKayitId)
{
    // HASTANIN SINIFI, "EN KOTU DURUM KAZANIR" KURALIYLA. Ust karttaki sayilar da,
    // bir karta tiklandiginda listelenecek hastalar da bu tek kuraldan geliyor --
    // boylece kartta yazan sayi ile listelenen hasta sayisi birbirini tutuyor.
    public SyncStatus Sinif => Hatali > 0 ? SyncStatus.Failed
        : Atlanan > 0 ? SyncStatus.Skipped
        : SyncStatus.Success;
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

    public int ToplamKayit => Ozet.Kayitlar.Count;
    public bool Suzulmus => Kayitlar.Count < ToplamKayit;

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
