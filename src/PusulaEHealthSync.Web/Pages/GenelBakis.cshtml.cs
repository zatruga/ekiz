using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// ===================== GENEL BAKIS -- ANA OLCU: PROTOKOL (2026-10-09) =====================
//
// KULLANICI ISTEGI: "genel bakis alanini bir kontrol eder misin, burada ana kriter PROTOKOL
// olmali, sayilar protokol ozelinde listelemeli, altinda satir sayisi olabilir."
//
// ONCEKI HALIN SORUNU UC AYRI BIRIMI AYNI EKRANDA KARISTIRMASIYDI. Sunucu gunlugunde
// olculdu (2026-10-08 kopyasi):
//
//   04.10.2026  ->  73.402 SATIR  =  4.452 KAYIT  =  77 PROTOKOL
//
// Trend grafigi o gun icin 73.402 ciziyordu; protokol sayisinin 953 katini. Ust kartlardaki
// "Gonderilen Kayit Orani" ve "Gonderilemeyen Kayit" kayit sayiyordu (2026-10-08'de
// duzeltilmisti), "Hata Kategorileri" ve "Hazir Olmayan Kayitlar" ise hala SATIR
// sayiyordu -- 808 satir = 46 kayit, yani kategori kutucuklari 17,6 kat sisikti.
//
// IKI AYRI HATA DAHA BULUNDU:
//
//   1) KAYNAK TURU KARTI ALTI TUR IZLIYORDU, SYNCLOG'DA ON IKI TUR VAR. Gorunmeyenler:
//      Observation (12.942 kayit -- EN BUYUK KALEM), DiagnosticReport, patoloji ucluSU,
//      Observation-Vital. Yani kart tum isin %63'unu hic gostermiyordu. Laboratuvar
//      haftada ~42.000 kayit gonderiyor ve ekranda izi yoktu.
//
//   2) SEBEPLER HAM MESAJA GORE GRUPLANIYORDU. Mesaj panel adini ve tarihini de tasidigi
//      icin ayni sebep onlarca satira boluniyordu ("Idrar Mikroskopisi (01.08.2026)",
//      "(03.08.2026)", ...). Ekranda alti kutucuk vardi ve altisi da AYNI sebebi
//      anlatiyordu. Artik SyncLogEntry.ErrorCategory ile kategori bazinda gruplaniyor.
//
// SIMDI: her sayinin birimi belli ve ekranda YAZIYOR. Ana rakam PROTOKOL, altinda
// "N kayit - M satir". Hesap ProtokolDurumService'te, tek yerde (bkz. o dosyanin basi).
public class GenelBakisModel(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    ProtokolDurumService protokolDurum,
    IcbariKapsamService icbariKapsam,
    PendingWorkService pendingWork,
    SettingsStore settings,
    IMemoryCache onbellek,
    ILogger<GenelBakisModel> logger) : PageModel
{
    // ===================== UST DURUM ALANI (2026-10-24, "A + B'nin ust seridi") =====================
    //
    // KULLANICI: tasarim tuvalindeki A yonu ("Hat") secildi, ustune B'deki gonderim durum
    // alani eklenecek. Veri Aktivite Akisi seridiyle AYNI okumadan (CanliSerit.OkuAsync) --
    // iki ekran "gonderim calisiyor mu" sorusuna farkli cevap veremez. ONBELLEGE ALINMIYOR:
    // "su an" iki dakika bayat olamaz.
    public CanliSerit Serit { get; private set; } = CanliSerit.Bos;

    // Son 12 saat, saat basina dokunulan / hata alan protokol -- eksik saatler 0 ile dolu.
    public List<SyncLogStore.SaatOzeti> Saatler { get; private set; } = [];
    public int SaatMaks => Saatler.Count == 0 ? 0 : Saatler.Max(s => s.Protokol);
    public int Son12SaatProtokol => Saatler.Sum(s => s.Protokol);

    // Hat'in 2. duragi (tarama) AKTIF ortamin gunlugune bakiyor. Ortam canli degilse hat
    // sandbox'i anlatirdi -- sayfa yalnizca canliyi gosterdigi icin o durumda gizleniyor.
    public bool AktifOrtamCanli => Serit.Canli;

    // "Geriye ne kaldi" karti: bekleyen kalemler turune gore (Bekleyen Isler ile ayni nesne).
    public List<KeyValuePair<string, int>> BekleyenTurler { get; private set; } = [];

    // Hat'in 1. duragi -- null: Pusula'ya erisilemedi ya da tarama henuz yok.
    public int? TaramaAraligindaAcilan { get; private set; }

    // Trend penceresi. Donem bundan uzunsa pencere donemle ayni olur -- boylece TEK
    // protokol cozumu hem donem toplamlarini hem gunluk grafigi besliyor.
    private const int TrendGun = 14;

    // ONBELLEK: protokol cozumu Pusula'ya ~21 sorgu atiyor (olculdu: son 14 gunde 20.819
    // farkli cocuk id). Her F5'te tekrarlamak hem yavas hem gereksiz -- Aktivite Akisi
    // ayni deseni ayni sure ile kullaniyor.
    private static readonly TimeSpan OnbellekOmru = TimeSpan.FromMinutes(2);

    // Bundan eski sonuc gosterilmez, hesap beklenir (bkz. TazeOnbellek).
    private static readonly TimeSpan OnbellekEnEski = TimeSpan.FromMinutes(10);

    // "0" Bugün | "7" Son 7 Gün | "30" Son 30 Gün | "ay:yyyy-MM" belirli bir ay
    // (KULLANICI ISTEGI 2026-10-13: "buraya ay secimi yapacagimiz bir check list olsun,
    // oradan secilen ayin basi ile sonu listelensin").
    [BindProperty(SupportsGet = true)]
    public string Donem { get; set; } = "7";

    public DateOnly PeriodFromDate { get; private set; }
    public DateOnly PeriodToDate { get; private set; }
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    // Donem secicide gosterilecek son 12 ay. Ay adlari SABIT LISTEDEN geliyor,
    // CultureInfo'dan degil: uygulama istek kulturunu ayarlamiyor, dolayisiyla
    // ToString("MMMM") sunucunun kulturune gore sessizce Ingilizceye dusebilirdi.
    private static readonly string[] AyAdlari =
    [
        "Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran",
        "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık",
    ];

    public record AySecenegi(string Deger, string Etiket, bool Secili);

    public List<AySecenegi> AylarListesi { get; private set; } = [];
    public bool AySecili => Donem.StartsWith("ay:", StringComparison.Ordinal);

    // ===================== BEKLEYEN ISLER KAPSAMI (2026-10-13) =====================
    //
    // KULLANICI: "genel bakis ile bekleyen isler ekraninda sayilar ortusmuyor, genel
    // bakista gonderimi yapilan protokol 2936 iken bekleyen islerde 4500."
    //
    // ORTUSMEMELERI DOGRU -- IKI AYRI SORU SORULUYOR:
    //
    //   Genel Bakis  : SyncLog'a BU PENCEREDE yazilan satirlardan yola cikar.
    //                  "Bu donemde yaptigimiz gonderimlerin kaci sorunsuz bitti?"
    //                  HIC DENENMEMIS bir kalemi GOREMEZ -- SyncLog satiri yoktur.
    //   Bekleyen Isler: Pusula'ya "bu aralikta ne var?" diye sorar, sonra her kalemi
    //                  SyncLog'da arar. "Pusula'da hazir olanin kaci TRƏS'te tam?"
    //
    // Uc fark birden: (a) zaman ekseni -- biri GONDERIM tarihi, digeri SONUCLANMA
    // tarihi; (b) tanim -- "bu pencerede hata almadi" ile "tek bir kalemi bile eksik
    // degil" ayni sey degil; (c) aralik -- iki sayfa ayni araligi secmek zorunda degil.
    //
    // COZUM AYNI HESABI UCUNCU KEZ YAZMAK DEGIL: PendingWorkService singleton ve
    // SonSonuc zaten bellekte duruyor (GunSonuRaporService de oradan okuyor). Ayni
    // NESNE basiliyor, dolayisiyla iki ekran yapisal olarak ayrisamaz.
    public PendingKapsam? Kapsam { get; private set; }
    public DateTime? KapsamZamaniUtc { get; private set; }
    public DateTime? KapsamBaslangic { get; private set; }
    public DateTime? KapsamBitis { get; private set; }

    public BakisOzeti Ozet { get; private set; } = BakisOzeti.Bos;

    // ---- Trend grafigi geometrisi (sunucu tarafinda hesaplaniyor) ----
    // KURAL: hasta/hata verisi hic JS'e gecmiyor -- mockup'taki client-side surum
    // bilincli olarak sunucuya tasindi (no external CDN/font/library, bkz. site.css).
    public string TrendTamamPath { get; private set; } = "";
    public string TrendSorunluPath { get; private set; } = "";
    public string TrendAreaPath { get; private set; } = "";
    public List<TrendLabel> TrendLabels { get; private set; } = [];
    public double TrendLastX { get; private set; }
    public double TrendLastY { get; private set; }
    public int TrendMaks { get; private set; }

    public record TrendLabel(double X, string Text, string Anchor);

    // ======================= OZET SOZLESMESI =======================
    //
    // UC BIRIM, HER BIRI ADIYLA: Protokol (ana), Kayit (alt), Satir (en alt).
    // Bir alan adinda birimini tasimiyorsa, bu dosyada bir hata vardir.
    public record BakisOzeti(
        // -- PROTOKOL: donemde gonderim denemesi yapilan protokoller --
        int ProtokolDokunulan, int ProtokolTamam, int ProtokolEksik, int ProtokolHatali,
        double TamamYuzde, double TamamYuzdeDelta, bool DeltaVar,
        int KayitToplam, int SatirToplam, int ProtokolsuzKayit,

        // -- PUSULA HACMI: donemde ACILAN protokoller (ayri bir soru, ayri bir kume) --
        int AcilanProtokol, int AcilanBugun, double AcilanDegisimYuzde, bool AcilanTabanVar,
        // Gecmis bir ay secildiginde "bugun acilan" anlamsiz -- o zaman gunluk ortalama
        // gosteriliyor. BugunDonemde, ekranin hangisini yazacagina karar veriyor.
        double AcilanGunlukOrtalama, bool BugunDonemde,

        // -- GONDERIM SAGLIGI: pencerenin kac gununde hic gonderim var --
        int DoluGun, int PencereGun, DateOnly? SonGonderimGunu,

        List<GunNoktasi> Trend,
        List<TurSatiri> Turler,
        List<SebepSatiri> HataSebepleri,
        List<SebepSatiri> EksikSebepleri,
        List<HataSatiri> SonHatalar,
        List<BolumSatiri> Bolumler,

        // Icbari kapsami -- hesap IcbariKapsamService'te, Icbari Islemler sayfasiyla ortak.
        IcbariKapsamService.IcbariOzeti Icbari,

        bool Kirpildi, bool PusulaHatasi,

        List<EpikrizDoktorSatiri> EpikrizDoktorlari,

        // Sebep kartlarindaki "Listele" penceresinin kaynagi -- ayni hesaptan, ayni
        // onbellekte (kartta yazan sayi ile listelenen kayit ayrisamaz).
        List<SebepKaydi> SebepKayitlari)
    {
        public static readonly BakisOzeti Bos = new(
            0, 0, 0, 0, 0, 0, false, 0, 0, 0,
            0, 0, 0, false, 0, true,
            0, 0, null,
            [], [], [], [], [], [],
            IcbariKapsamService.IcbariOzeti.Bos, false, false,
            [], []);

        public int ProtokolSorunlu => ProtokolEksik + ProtokolHatali;
        public double RingDashOffset => 119.4 * (1 - TamamYuzde / 100.0);
    }

    public record GunNoktasi(DateOnly Gun, int Tamam, int Eksik, int Hatali)
    {
        public int Toplam => Tamam + Eksik + Hatali;
    }

    // Kaynak turu kirilimi. PROTOKOL sayisi ana rakam; ama Practitioner gibi protokole
    // BAGLANAMAYAN turlerde protokol sifir kalir -- o satirlar kayit sayisiyla gosterilir
    // (Protokolsuz = true). Sifir yazip gecmek, o turu yok saymak olurdu.
    public record TurSatiri(string ResourceType, string Etiket,
                            int ProtokolTamam, int ProtokolEksik, int ProtokolHatali,
                            int KayitBasarili, int KayitEksik, int KayitHatali, int Satir)
    {
        public int Protokol => ProtokolTamam + ProtokolEksik + ProtokolHatali;
        public int Kayit => KayitBasarili + KayitEksik + KayitHatali;
        public bool Protokolsuz => Protokol == 0;

        // Cubuk, protokole baglanabilen turlerde PROTOKOL oranini gosteriyor; digerlerinde
        // kayit oranini. Ikisini ayni cubukta karistirmamak icin ekran hangisini
        // gosterdigini yaziyor.
        public int BarTamam => Protokolsuz ? KayitBasarili : ProtokolTamam;
        public int BarEksik => Protokolsuz ? KayitEksik : ProtokolEksik;
        public int BarHatali => Protokolsuz ? KayitHatali : ProtokolHatali;
        public int BarToplam => BarTamam + BarEksik + BarHatali;
        public double TamamYuzde => BarToplam == 0 ? 0 : Math.Round(BarTamam * 100.0 / BarToplam, 1);
    }

    // Sebep kutucugu. HAM MESAJ DEGIL KATEGORI -- ham mesaj panel adi/tarih tasidigi icin
    // ayni sebebi onlarca parcaya boluyordu (bkz. dosya basindaki not 2).
    // Turler: sebebin HANGI KAYIT TURLERINDEN geldigi, tur basina kayit sayisiyla
    // (2026-10-24, kullanici: "burada hangi veri turunun oldugunu da belirtelim").
    // "Gonderilecek metin yok" tek basina epikriz mi patoloji mi belli etmiyordu.
    public record SebepSatiri(string Kategori, string Aciklama, int Protokol, int Kayit, int Satir,
                              List<(string ResourceType, string Etiket, int Kayit)> Turler);

    // SEBEP LISTESI (2026-10-26, kullanici: "eslestirme eksik satirinda bu hizmetleri ya da
    // testleri liste seklinde vermeli; kimlik numarasi yok kisminda listele deyince liste
    // popup ekranda gostermeli; tetkik sonucu girilmedi denilen hastalari listelemeli").
    // Kayit basina bir satir (kayit-gun degil); Durum "eksik" | "hata".
    public record SebepKaydi(string Durum, string Kategori, long SonId, string ResourceType,
                             int PusulaId, int? ProtokolId, string? Mesaj, DateOnly Gun);

    public record HataSatiri(long SonId, int? ProtokolId, string HastaAdi, string TurEtiketi,
                             string HamMesaj, string AnlasilirMesaj, int Kayit, DateOnly Gun);

    public record BolumSatiri(string Ad, int Protokol, int Tamam, int Eksik, int Hatali, int Pct);


    // ===================== EPIKRIZ -- DOKTOR BAZINDA (2026-10-24) =====================
    //
    // KULLANICI: "epikriz eksik olanlarin doktor bazli oranlamasi, kac protokolde basari
    // durumu -- bunun sayesinde doktorlara da baski yapilabilir."
    //
    // DOKTOR = PROTOKOLUN SORUMLU DOKTORU (ProtokolListItem.Doktor*), epikrizi yazan degil:
    // eksik epikrizde yazan kisi zaten yok (metin bos), hesap soracak kisi protokolun sahibi.
    // PAYDA: donemde epikriz kaydi DENENEN protokoller. Epikrizi hic olmayan protokol
    // (ayaktan muayenelerin cogu) sayilmiyor -- yoksa her doktor haksiz yere dusuk cikardi.
    // Protokol basina "en kotu durum kazanir", sayfanin geri kalaniyla ayni kural.
    public record EpikrizDoktorSatiri(string Doktor, string Bolum, int Protokol, int Gonderildi,
                                      int Eksik, int Hatali, List<int> SorunluProtokoller)
    {
        public double BasariYuzde => Protokol == 0 ? 0 : Math.Round(Gonderildi * 100.0 / Protokol, 1);
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = Today;

        // AY LISTESI her istekte kuruluyor -- gun degisince kendiliginden kayiyor.
        var buAy = new DateOnly(today.Year, today.Month, 1);
        AylarListesi = Enumerable.Range(0, 12)
            .Select(i => buAy.AddMonths(-i))
            .Select(a =>
            {
                var deger = $"ay:{a:yyyy-MM}";
                return new AySecenegi(deger, $"{AyAdlari[a.Month - 1]} {a.Year}", Donem == deger);
            })
            .ToList();

        // GECERSIZ AY = VARSAYILAN DONEM. Elle yazilmis bir sorgu dizesi ("ay:abc")
        // sayfayi patlatmamali; sessizce "Son 7 Gun"e dusuyor.
        var ay = AyAraligi(Donem, today);
        if (Donem.StartsWith("ay:", StringComparison.Ordinal) && ay is null) Donem = "7";

        (PeriodFromDate, PeriodToDate) = ay ?? Donem switch
        {
            "0" => (today, today),
            "30" => (today.AddDays(-29), today),
            _ => (today.AddDays(-6), today),
        };

        // BEKLEYEN ISLER KAPSAMI -- ONBELLEGIN DISINDA, bellekteki nesneden okunuyor
        // (bedeli yok) ve DONEMDEN BAGIMSIZ. Donem onbellegine koysaydik, kullanici
        // donemi degistirince iki dakika boyunca eski tarama sonucunu gorurdu.
        Kapsam = pendingWork.SonSonuc?.Kapsam;
        KapsamZamaniUtc = pendingWork.SonHesaplamaUtc;
        KapsamBaslangic = pendingWork.SonTaramaBaslangici;
        KapsamBitis = pendingWork.SonTaramaBitisi;
        BekleyenTurler = pendingWork.SonSonuc?.OzetSayimlar
            .Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ToList() ?? [];

        // HAT'IN 1. DURAGI: taramanin araliginda Pusula'da acilan protokol. Ayni aralik --
        // donemin degil -- yoksa ilk durak ile ikincisi farkli kumeleri anlatirdi.
        if (KapsamBaslangic is { } kb && KapsamBitis is { } kbit)
        {
            try
            {
                TaramaAraligindaAcilan = await onbellek.GetOrCreateAsync(
                    $"genelbakis-acilan|{kb:O}|{kbit:O}", async giris =>
                    {
                        giris.AbsoluteExpirationRelativeToNow = OnbellekOmru;
                        return (await pusulaRepository.GetProtokolListAsync(kb, kbit, null, ct)).Count;
                    });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Genel Bakis: tarama araligindaki acilan protokol sayisi okunamadi.");
            }
        }

        Serit = await CanliSerit.OkuAsync(settings, ct);
        var simdiSaat = DateTime.UtcNow;
        simdiSaat = new DateTime(simdiSaat.Year, simdiSaat.Month, simdiSaat.Day, simdiSaat.Hour, 0, 0, DateTimeKind.Utc);
        var ilkSaat = simdiSaat.AddHours(-11);
        var saatlik = (await syncLog.GetSaatlikProtokolSayilariAsync(ilkSaat, "Live", ct))
            .ToDictionary(s => s.SaatUtc);
        Saatler = Enumerable.Range(0, 12)
            .Select(i => ilkSaat.AddHours(i))
            .Select(h => saatlik.GetValueOrDefault(h) ?? new SyncLogStore.SaatOzeti(h, 0, 0))
            .ToList();

        var anahtar = $"genelbakis|{Donem}|{today:yyyy-MM-dd}";
        // Bayatsa once gosterilir, arkada tazelenir (bkz. TazeOnbellek).
        // PUSULA PATLADIYSA ONBELLEGE ALMA: eksik bir ozeti iki dakika boyunca
        // dogru gibi gostermek, yavas bir sayfadan kotu.
        Ozet = await TazeOnbellek.AlAsync(onbellek, anahtar, HesaplaAsync,
            OnbellekOmru, OnbellekEnEski, o => !o.PusulaHatasi, logger);

        TrendGeometrisiKur();
    }

    private async Task<BakisOzeti> HesaplaAsync(CancellationToken ct)
    {
        var today = Today;

        // ---- 1) PENCERE: donem ILE trend penceresinin birlesimi ----
        // Trend her zaman en az 14 gun gosteriyor; donem daha uzunsa (30 gun) pencere
        // donemle ayni olur. Boylece TEK protokol cozumu iki isi de besliyor.
        //
        // PENCERE DONEMIN BITISINDE BITIYOR, BUGUNDE DEGIL -- 2026-10-13'te degisti.
        // Ay secimi eklenince eski hal (her zaman bugune kadar) gecmis bir ay icin
        // yanlis oluyordu: Eylul secildiginde trend Eylul basindan BUGUNE kadar
        // ciziliyor, aradaki Ekim gunleri de "gonderim yapilmamis gun" sayiliyordu.
        var donemGun = PeriodToDate.DayNumber - PeriodFromDate.DayNumber + 1;
        var pencereGun = Math.Max(TrendGun, donemGun);
        var pencereSon = PeriodToDate;
        var pencereBas = pencereSon.AddDays(-(pencereGun - 1));

        // DIKKAT -- IKI ZAMAN TABANI (bkz. AzTime): Pusula (SQL Server) YEREL saat,
        // SyncLog (SQLite) UTC. 2026-09-09'da bu kafa karisikligi gun sinirini fiilen
        // 04:00'e kaydirmisti; artik yerel degerler Pusula'ya, cevrilenler SyncLog'a.
        var donemBasYerel = PeriodFromDate.ToDateTime(TimeOnly.MinValue);
        var donemBitYerelHaric = PeriodToDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var pencereBasUtc = AzTime.ToUtc(pencereBas.ToDateTime(TimeOnly.MinValue));
        var donemBitUtc = AzTime.ToUtc(donemBitYerelHaric);

        // BAGIMSIZ PUSULA SORGULARI SIMDI BASLIYOR, sonuclari asagida (10 ve 11. adimlar)
        // bekleniyor (2026-10-24). Ikisi de yalnizca donem tarihine bagli; ozetin
        // hesaplanmasini beklemeleri icin bir sebep yok. Hatalari kendi try bloklarinda,
        // await aninda yakalaniyor -- davranis sirali haliyle ayni.
        var acilanGorevi = pusulaRepository.GetProtokolListAsync(donemBasYerel, donemBitYerelHaric, null, ct);
        var icbariGorevi = icbariKapsam.KalemlerAsync(donemBasYerel, donemBitYerelHaric, "Live", ct);

        // ---- 2) PROTOKOL BAZLI OZET (pencerenin tamami) ----
        // YALNIZCA CANLI (2026-10-24, kullanici karari: "dashboard ekrani sadece canli
        // gonderimler uzerinden hesaplamalari yapsin, sandbox icin bir veri gosterme").
        // Sandbox gecmisine bakmak isteyen Aktivite Akisi'ndaki ortam seciciyi kullanir.
        var pencere = await protokolDurum.HesaplaAsync(pencereBasUtc, donemBitUtc, ct, "Live");

        // ---- 3) DONEME DUSEN KAYITLAR ----
        // Pencere donemden uzun olabilir; donem kartlari yalnizca donemin gunlerini sayar.
        var donemKayitlari = pencere.Kayitlar
            .Where(k => k.Gun >= PeriodFromDate && k.Gun <= PeriodToDate)
            .ToList();

        // Protokol sinifi DONEM icinde yeniden hesaplaniyor -- pencere sinifini kullanmak,
        // donem disindaki bir hatayi doneme tasirdi.
        var donemProtokolleri = ProtokolleriSinifla(donemKayitlari);

        var tamam = donemProtokolleri.Count(p => p.Value == ProtokolSinifi.Tamam);
        var eksik = donemProtokolleri.Count(p => p.Value == ProtokolSinifi.Eksik);
        var hatali = donemProtokolleri.Count(p => p.Value == ProtokolSinifi.Hatali);
        var dokunulan = donemProtokolleri.Count;
        var tamamYuzde = dokunulan == 0 ? 0 : Math.Round(tamam * 100.0 / dokunulan, 1);

        var kayitToplam = donemKayitlari.Select(k => (k.ResourceType, k.PusulaId)).Distinct().Count();
        var satirToplam = donemKayitlari.Sum(k => k.Satir);
        var protokolsuz = donemKayitlari
            .Where(k => k.ProtokolId is null)
            .Select(k => (k.ResourceType, k.PusulaId)).Distinct().Count();

        // ---- 4) ONCEKI DONEMLE KARSILASTIRMA ----
        // ONCEKI DONEM ICIN AYRI BIR PROTOKOL COZUMU YAPILMIYOR -- bu, Pusula'ya atilan
        // sorgu sayisini ikiye katlardi. Pencere donemden uzun oldugu surece onceki
        // donemin kayitlari ELIMIZDE zaten (14 gunluk pencere, 7 gunluk donem). Pencere
        // yetmiyorsa (30 gunluk donem) karsilastirma GOSTERILMIYOR -- yanlis bir taban
        // yerine hic taban.
        var oncekiBas = PeriodFromDate.AddDays(-donemGun);
        var deltaVar = oncekiBas >= pencereBas;
        double deltaYuzde = 0;
        if (deltaVar)
        {
            var oncekiKayitlar = pencere.Kayitlar
                .Where(k => k.Gun >= oncekiBas && k.Gun < PeriodFromDate).ToList();
            var onceki = ProtokolleriSinifla(oncekiKayitlar);
            if (onceki.Count == 0) deltaVar = false;
            else
            {
                var oncekiYuzde = onceki.Count(p => p.Value == ProtokolSinifi.Tamam) * 100.0 / onceki.Count;
                deltaYuzde = Math.Round(tamamYuzde - oncekiYuzde, 1);
            }
        }

        // ---- 5) GUNLUK TREND (protokol birimi) ----
        var gunBasina = new Dictionary<DateOnly, Dictionary<int, ProtokolSinifi>>();
        foreach (var gun in pencere.Kayitlar.GroupBy(k => k.Gun))
            gunBasina[gun.Key] = ProtokolleriSinifla(gun.ToList());

        var trend = new List<GunNoktasi>();
        for (var g = pencereBas; g <= pencereSon; g = g.AddDays(1))
        {
            var s = gunBasina.GetValueOrDefault(g);
            trend.Add(new GunNoktasi(g,
                s?.Count(p => p.Value == ProtokolSinifi.Tamam) ?? 0,
                s?.Count(p => p.Value == ProtokolSinifi.Eksik) ?? 0,
                s?.Count(p => p.Value == ProtokolSinifi.Hatali) ?? 0));
        }

        // GONDERIM SAGLIGI. Olculdu (sunucu, 25.09-08.10): 14 gunun YALNIZCA 7'sinde
        // gonderim var. Kullanici "otomatik gonderimi sanki surekli yapmiyor gibi" diye
        // sormustu ve haklydi -- ama ekranda bunu soyleyen hicbir sey yoktu; trend
        // grafigi sifire inip cikiyordu ve bu "o gun is yoktu" gibi okunuyordu.
        var doluGun = trend.Count(t => t.Toplam > 0);
        var sonGonderim = trend.Where(t => t.Toplam > 0).Select(t => (DateOnly?)t.Gun).LastOrDefault();

        // ---- 6) KAYNAK TURU KIRILIMI -- TUM TURLER ----
        // Sabit liste YOK: turler verinin kendisinden geliyor. Eski haldeki alti elemanli
        // sabit dizi, yeni bir modul (laboratuvar, radyoloji, patoloji, vital) eklendikce
        // sessizce eksik kaliyordu.
        var turler = donemKayitlari
            .GroupBy(k => k.ResourceType)
            .Select(g =>
            {
                var turProtokolleri = ProtokolleriSinifla(g.ToList());
                return new TurSatiri(g.Key, SyncLogEntry.ResourceTypeLabel(g.Key),
                    turProtokolleri.Count(p => p.Value == ProtokolSinifi.Tamam),
                    turProtokolleri.Count(p => p.Value == ProtokolSinifi.Eksik),
                    turProtokolleri.Count(p => p.Value == ProtokolSinifi.Hatali),
                    TekilSay(g, SyncStatus.Success),
                    TekilSay(g, SyncStatus.Skipped),
                    TekilSay(g, SyncStatus.Failed),
                    g.Sum(k => k.Satir));
            })
            .OrderByDescending(t => t.Kayit)
            .ToList();

        // ---- 7) SEBEPLER -- KATEGORI BAZINDA, HER UC BIRIMLE ----
        var hataSebepleri = SebepleriTopla(donemKayitlari, SyncStatus.Failed);
        var eksikSebepleri = SebepleriTopla(donemKayitlari, SyncStatus.Skipped);
        var sebepKayitlari = donemKayitlari
            .Where(k => k.Durum is SyncStatus.Skipped or SyncStatus.Failed)
            .GroupBy(k => (k.Durum, k.ResourceType, k.PusulaId))
            .Select(g => g.MaxBy(k => k.SonId)!)
            .Select(k => new SebepKaydi(k.Durum == SyncStatus.Failed ? "hata" : "eksik",
                SyncLogEntry.ErrorCategory(k.Mesaj).Label, k.SonId, k.ResourceType, k.PusulaId,
                k.ProtokolId, k.Mesaj, k.Gun))
            .ToList();

        // ---- 8) SON HATALAR ----
        // Hasta adi artik AYRI BIR SYNCLOG SORGUSUNDAN gelmiyor: protokol bilgileri
        // zaten elimizde (ProtokolBilgileri). Eski hal 500 satir cekip grupluyordu ve
        // o 500'u tum resim gibi gosteriyordu.
        var sonHatalar = donemKayitlari
            .Where(k => k.Durum == SyncStatus.Failed)
            .GroupBy(k => (k.ProtokolId, k.ResourceType, k.Mesaj))
            .Select(g =>
            {
                var enYeni = g.OrderByDescending(x => x.SonId).First();
                var hastaAdi = enYeni.ProtokolId is { } pid
                    && pencere.ProtokolBilgileri.TryGetValue(pid, out var pr)
                    && !string.IsNullOrWhiteSpace(pr.HastaAdiSoyadi)
                    ? pr.HastaAdiSoyadi : "-";
                return new HataSatiri(enYeni.SonId, enYeni.ProtokolId, hastaAdi,
                    SyncLogEntry.ResourceTypeLabel(enYeni.ResourceType),
                    enYeni.Mesaj ?? "Sebep belirtilmedi",
                    SyncLogEntry.FriendlyError(enYeni.Mesaj),
                    g.Select(x => (x.ResourceType, x.PusulaId)).Distinct().Count(),
                    enYeni.Gun);
            })
            .OrderByDescending(h => h.Gun).ThenByDescending(h => h.SonId)
            .Take(8)
            .ToList();

        // ---- 9) BOLUM KIRILIMI -- GONDERILEN protokollerin bolumu ----
        // ESKI HAL BASKA BIR SORUYU CEVAPLIYORDU: donemde ACILAN protokolleri bolume
        // gore sayiyordu, yani hastane is hacmini. Bu bir GONDERIM panosu; burada
        // ilgilendigimiz sey gonderimi yapilan protokollerin hangi bolumlerde sorun
        // cikardigi. Hacim rakami ust kartta ("Dönemde Açılan Protokol") duruyor.
        var bolumler = donemProtokolleri
            .Select(p => (
                Bolum: pencere.ProtokolBilgileri.TryGetValue(p.Key, out var pr)
                    && !string.IsNullOrWhiteSpace(pr.BolumAdi) ? pr.BolumAdi! : "Bölümsüz",
                Sinif: p.Value))
            .GroupBy(x => x.Bolum)
            .Select(g => new
            {
                Ad = g.Key,
                Protokol = g.Count(),
                Tamam = g.Count(x => x.Sinif == ProtokolSinifi.Tamam),
                Eksik = g.Count(x => x.Sinif == ProtokolSinifi.Eksik),
                Hatali = g.Count(x => x.Sinif == ProtokolSinifi.Hatali),
            })
            .OrderByDescending(g => g.Protokol)
            // TAVAN YOK (2026-10-26, kullanici: "tum bolumler listelensin, kart kendi icinde
            // kaysin"). Kart sabit yukseklikte, liste icinde kaydiriliyor.
            .ToList();
        var enBuyukBolum = bolumler.Count == 0 ? 0 : bolumler[0].Protokol;
        var bolumSatirlari = bolumler
            .Select(b => new BolumSatiri(b.Ad, b.Protokol, b.Tamam, b.Eksik, b.Hatali,
                enBuyukBolum == 0 ? 0 : (int)Math.Round(b.Protokol * 100.0 / enBuyukBolum)))
            .ToList();

        // ---- 9b) EPIKRIZ -- DOKTOR BAZINDA (bkz. EpikrizDoktorSatiri) ----
        var epikrizDoktorlari = ProtokolleriSinifla(
                donemKayitlari.Where(k => k.ResourceType == "Composition").ToList())
            .Select(p => (
                Bilgi: pencere.ProtokolBilgileri.GetValueOrDefault(p.Key),
                ProtokolId: p.Key, Sinif: p.Value))
            .GroupBy(x => x.Bilgi?.DoktorAdiSoyadi is { Length: > 0 } d ? d : "Doktor bilinmiyor")
            .Select(g => new EpikrizDoktorSatiri(
                g.Key,
                g.Select(x => x.Bilgi?.BolumAdi).Where(b => !string.IsNullOrWhiteSpace(b))
                 .GroupBy(b => b).OrderByDescending(b => b.Count()).Select(b => b.Key!).FirstOrDefault() ?? "-",
                g.Count(),
                g.Count(x => x.Sinif == ProtokolSinifi.Tamam),
                g.Count(x => x.Sinif == ProtokolSinifi.Eksik),
                g.Count(x => x.Sinif == ProtokolSinifi.Hatali),
                g.Where(x => x.Sinif != ProtokolSinifi.Tamam).Select(x => x.ProtokolId).OrderBy(i => i).ToList()))
            .OrderByDescending(d => d.Eksik + d.Hatali)
            .ThenBy(d => d.BasariYuzde)
            .ThenByDescending(d => d.Protokol)
            .ToList();

        // ---- 10) PUSULA HACMI: donemde ACILAN protokol ----
        var pusulaHatasi = pencere.PusulaHatasi;
        var bugunDonemde = PeriodToDate >= today;
        var acilan = 0;
        var acilanBugun = 0;
        double acilanDegisim = 0;
        var acilanTabanVar = false;
        try
        {
            var donemProtokolListesi = await acilanGorevi;
            acilan = donemProtokolListesi.Count;

            // GECMIS BIR AY SECILDIYSE "bugun" donemin DISINDA: ne "bugun acilan"in
            // ne de "dune gore" karsilastirmasinin anlami var. Ekran o durumda gunluk
            // ortalamayi yaziyor (bkz. BakisOzeti.AcilanGunlukOrtalama).
            //
            // TUM BLOK KOSULUN ICINDE: ilk yazimda yalnizca degerler sifirlaniyordu,
            // ama asagidaki "dun" hesabi acilanTabanVar'i hemen ardindan yeniden
            // yaziyordu. Sonuc tesadufen dogruydu (gecmis bir ayin listesinde dunku
            // protokol bulunmadigi icin dun = 0 cikiyordu); kosula almak bunu
            // tesadufen degil kurulus geregi dogru yapiyor ve bir Pusula sorgusunu
            // da bosuna atmiyor.
            if (bugunDonemde)
            {
                var bugunBas = today.ToDateTime(TimeOnly.MinValue);
                var yarinBas = today.AddDays(1).ToDateTime(TimeOnly.MinValue);
                acilanBugun = Donem == "0"
                    ? acilan
                    : donemProtokolListesi.Count(p => p.AcilisTarihi >= bugunBas && p.AcilisTarihi < yarinBas);

                var dunBas = today.AddDays(-1).ToDateTime(TimeOnly.MinValue);
                var dun = donemProtokolListesi.Count(p => p.AcilisTarihi >= dunBas && p.AcilisTarihi < bugunBas);
                // Donem "Bugün" ise dunku liste elimizde degil, ayrica sorulur.
                if (Donem == "0")
                    dun = (await pusulaRepository.GetProtokolListAsync(dunBas, bugunBas, null, ct)).Count;
                acilanTabanVar = dun > 0;
                acilanDegisim = dun == 0 ? 0 : Math.Round((acilanBugun - dun) * 100.0 / dun, 1);
            }
        }
        catch (Exception ex)
        {
            pusulaHatasi = true;
            logger.LogWarning(ex, "Genel Bakis: donemde acilan protokol sayisi okunamadi.");
        }

        // ---- 11) ICBARI KAPSAMI ----
        // YEREL SAAT, UTC DEGIL: sorgu p.AcilisTarihi (Pusula yerel saati) uzerinde
        // (bkz. yukaridaki icbariGorevi). HASTA LISTESI YOK (2026-10-26, kullanici: "hasta
        // listesi olarak degil rakamlara odaklanalim, tiklayinca filtreli sayfaya gidelim")
        // -- satirlar Icbari Islemler sayfasinda, ayni servisten.
        var icbari = IcbariKapsamService.IcbariOzeti.Bos;
        try
        {
            icbari = IcbariKapsamService.Ozetle(await icbariGorevi);
        }
        catch (Exception ex)
        {
            pusulaHatasi = true;
            logger.LogWarning(ex, "Genel Bakis: icbari kapsami okunamadi.");
        }

        return new BakisOzeti(
            dokunulan, tamam, eksik, hatali, tamamYuzde, deltaYuzde, deltaVar,
            kayitToplam, satirToplam, protokolsuz,
            acilan, acilanBugun, acilanDegisim, acilanTabanVar,
            donemGun == 0 ? 0 : Math.Round((double)acilan / donemGun, 1), bugunDonemde,
            doluGun, pencereGun, sonGonderim,
            trend, turler, hataSebepleri, eksikSebepleri, sonHatalar, bolumSatirlari,
            icbari,
            pencere.Kirpildi, pusulaHatasi,
            epikrizDoktorlari, sebepKayitlari);
    }

    // ===================== SEBEP LISTESI PENCERESI =====================
    //
    // Sayfadaki <dialog> bunu fetch ile cagiriyor; ozet onbellekten geliyor (sayfa az once
    // ayni anahtarla hesapladi). Pusula'ya yalnizca sayfadaki kayitlarin adi ve protokol
    // bilgisi icin gidiliyor -- SELECT.
    private const int ListeTavan = 3000;

    public record SebepListeSatiri(SebepKaydi Kayit, string TurEtiketi, string? Icerik,
                                   string? Hasta, string? Bolum, string? Doktor);

    // KONUYA GORE: eksik eslestirme, sonucsuz tetkik gibi sebeplerde asil soru "HANGI
    // hizmet/test" -- ayni tetkik yuzlerce hastada takiliyor. Kimlik/FIN sebeplerinde konu
    // hastanin kendisi. Konu = icerik, yoksa hasta. TUM kayitlar uzerinden (tavan yok) --
    // eksik tanim listesi yarim kalmasin.
    public record SebepKonusu(string Konu, string TurEtiketi, int Kayit, int Protokol);

    public record SebepListesi(string Durum, string Kategori, string Aciklama, int Toplam,
                               List<SebepKonusu> Konular,
                               List<SebepListeSatiri> Satirlar, bool Kirpildi, bool PusulaHatasi)
    {

        // Hasta odakli sebepler: once kayit listesi acilsin.
        public bool HastaOdakli => Kategori.Contains("Kimlik", StringComparison.OrdinalIgnoreCase)
                                || Kategori.Contains("FIN", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IActionResult> OnGetSebepListesiAsync(string durum, string kategori, CancellationToken ct)
    {
        var liste = await SebepListesiKurAsync(durum, kategori, ct);
        return Partial("_GbSebepListesi", liste);
    }

    public async Task<IActionResult> OnGetSebepExcelAsync(string durum, string kategori, CancellationToken ct)
    {
        var liste = await SebepListesiKurAsync(durum, kategori, ct);
        var sutunlar = new List<PusulaEHealthSync.Export.XlsxWriter.Sutun>
        {
            new("Tür", 16), new("İçerik", 44), new("Hasta", 28), new("Protokol", 12, Sayi: true),
            new("Bölüm", 28), new("Doktor", 26), new("Gün", 12), new("Mesaj", 80),
        };
        var satirlar = liste.Satirlar.Select(x => new string?[]
        {
            x.TurEtiketi, x.Icerik, x.Hasta, x.Kayit.ProtokolId?.ToString(CultureInfo.InvariantCulture),
            x.Bolum, x.Doktor, x.Kayit.Gun.ToString("dd.MM.yyyy"), x.Kayit.Mesaj,
        });
        var bayt = PusulaEHealthSync.Export.XlsxWriter.Olustur(liste.Kategori.Length > 30 ? liste.Kategori[..30] : liste.Kategori, sutunlar, satirlar);
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"sebep-{durum}-{PeriodFromDate:yyyyMMdd}-{PeriodToDate:yyyyMMdd}.xlsx");
    }

    private async Task<SebepListesi> SebepListesiKurAsync(string durum, string kategori, CancellationToken ct)
    {
        await OnGetAsync(ct);
        var tum = Ozet.SebepKayitlari.Where(k => k.Durum == durum && k.Kategori == kategori)
            .OrderByDescending(k => k.SonId).ToList();
        var secilen = tum.Take(ListeTavan).ToList();
        var aciklama = (durum == "hata" ? Ozet.HataSebepleri : Ozet.EksikSebepleri)
            .FirstOrDefault(x => x.Kategori == kategori)?.Aciklama ?? "";

        var pusulaHatasi = false;
        var icerikler = new Dictionary<(string, int), string>();
        foreach (var tip in tum.GroupBy(k => k.ResourceType))
        {
            if (!PusulaRepository.AciklamaDestekleniyorMu(tip.Key)) continue;
            try
            {
                foreach (var kv in await pusulaRepository.KayitAciklamalariAsync(
                             tip.Key, tip.Select(k => k.PusulaId).Distinct().ToList(), ct))
                    icerikler[(tip.Key, kv.Key)] = kv.Value;
            }
            catch (Exception ex)
            {
                pusulaHatasi = true;
                logger.LogWarning(ex, "Genel Bakis: {Tip} kayit adlari okunamadi.", tip.Key);
            }
        }

        Dictionary<int, ProtokolListItem> protokoller = [];
        try
        {
            protokoller = await pusulaRepository.GetProtokollerByIdsAsync(
                secilen.Where(k => k.ProtokolId is not null).Select(k => k.ProtokolId!.Value).Distinct().ToList(), ct);
        }
        catch (Exception ex)
        {
            pusulaHatasi = true;
            logger.LogWarning(ex, "Genel Bakis: sebep listesi icin protokol bilgileri okunamadi.");
        }

        var satirlar = secilen.Select(k =>
        {
            var pr = k.ProtokolId is { } pid ? protokoller.GetValueOrDefault(pid) : null;
            var icerik = icerikler.GetValueOrDefault((k.ResourceType, k.PusulaId));
            // Hasta kaydinin "icerigi" hastanin kendisi -- konu olarak degil hasta olarak gosterilir.
            var hasta = k.ResourceType == "Patient" ? icerik : pr?.HastaAdiSoyadi;
            if (k.ResourceType == "Patient") icerik = null;
            return new SebepListeSatiri(k, SyncLogEntry.ResourceTypeLabel(k.ResourceType), icerik,
                string.IsNullOrWhiteSpace(hasta) ? null : hasta, pr?.BolumAdi, pr?.DoktorAdiSoyadi);
        }).ToList();

        // Konu adi icerik yoksa hasta -- hasta adi yalnizca listelenen kayitlar icin cozuldu;
        // kalanlar icin protokol numarasi yazilir (hasta odakli sebepler zaten kucuk).
        var hastaAdlari = satirlar.Where(x => x.Kayit.ProtokolId is not null && x.Hasta is not null)
            .GroupBy(x => x.Kayit.ProtokolId!.Value).ToDictionary(g => g.Key, g => g.First().Hasta!);
        var konular = tum
            .GroupBy(k =>
            {
                var icerik = icerikler.GetValueOrDefault((k.ResourceType, k.PusulaId));
                var konu = k.ResourceType == "Patient" ? icerik
                    : icerik ?? (k.ProtokolId is { } pid
                        ? hastaAdlari.GetValueOrDefault(pid) ?? $"Protokol {pid}"
                        : null);
                return (Konu: konu ?? "(adı çözülemedi)", Tur: SyncLogEntry.ResourceTypeLabel(k.ResourceType));
            })
            .Select(g => new SebepKonusu(g.Key.Konu, g.Key.Tur, g.Count(),
                g.Where(k => k.ProtokolId is not null).Select(k => k.ProtokolId).Distinct().Count()))
            .OrderByDescending(k => k.Kayit).ThenBy(k => k.Konu)
            .ToList();

        return new SebepListesi(durum, kategori, aciklama, tum.Count, konular, satirlar, tum.Count > secilen.Count, pusulaHatasi);
    }

    // EXCEL: doktor bazli epikriz tablosunun TAMAMI (ekranda ilk 12 satir). Ayni donem,
    // ayni onbellekli ozet -- ekrandaki sayilarla indirilen dosya ayrisamaz.
    public async Task<IActionResult> OnGetEpikrizExcelAsync(CancellationToken ct)
    {
        await OnGetAsync(ct);
        var sutunlar = new List<PusulaEHealthSync.Export.XlsxWriter.Sutun>
        {
            new("Doktor", 32),
            new("Bölüm", 30),
            new("Epikrizli protokol", 18, Sayi: true),
            new("Gönderildi", 13, Sayi: true),
            new("Eksik (metin boş vb.)", 20, Sayi: true),
            new("Hatalı", 10, Sayi: true),
            new("Başarı %", 11, Sayi: true),
            new("Sorunlu protokoller", 60),
        };
        var satirlar = Ozet.EpikrizDoktorlari.Select(d => new string?[]
        {
            d.Doktor, d.Bolum,
            d.Protokol.ToString(CultureInfo.InvariantCulture),
            d.Gonderildi.ToString(CultureInfo.InvariantCulture),
            d.Eksik.ToString(CultureInfo.InvariantCulture),
            d.Hatali.ToString(CultureInfo.InvariantCulture),
            d.BasariYuzde.ToString("0.0", CultureInfo.InvariantCulture),
            string.Join(", ", d.SorunluProtokoller),
        });
        var bayt = PusulaEHealthSync.Export.XlsxWriter.Olustur("Epikriz - Doktor", sutunlar, satirlar);
        var ad = $"epikriz-doktor-{PeriodFromDate:yyyyMMdd}-{PeriodToDate:yyyyMMdd}.xlsx";
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ad);
    }

    // "ay:2026-10" -> o ayin ilk gunu .. son gunu. Gecerli degilse null.
    //
    // ICINDE BULUNULAN AY BUGUNDE KESILIYOR: kesilmeseydi ayin kalan gunleri de
    // pencereye girer ve "son 31 gunun 18'inde hic gonderim yapilmamis" gibi
    // her ay basinda kendiliginden kirmizilasan bir uyari cikardi.
    private static (DateOnly Bas, DateOnly Bit)? AyAraligi(string donem, DateOnly bugun)
    {
        if (!donem.StartsWith("ay:", StringComparison.Ordinal)) return null;
        if (!DateTime.TryParseExact(donem[3..], "yyyy-MM", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var ay)) return null;

        var bas = new DateOnly(ay.Year, ay.Month, 1);
        if (bas > bugun) return null;                       // gelecek ay -- veri yok
        var bit = bas.AddMonths(1).AddDays(-1);
        return (bas, bit > bugun ? bugun : bit);
    }

    // "EN KOTU DURUM KAZANIR" -- hata eksigi, eksik basariyi ezer. Ayni kural:
    // ProtokolDurumService, GunSonuRaporService, Aktivite Akisi, SyncLogStore kayit
    // sayimlari. Bes yerde ayni cevap.
    private static Dictionary<int, ProtokolSinifi> ProtokolleriSinifla(List<KayitDurumu> kayitlar)
    {
        var sonuc = new Dictionary<int, ProtokolSinifi>();
        foreach (var k in kayitlar)
        {
            if (k.ProtokolId is not { } pid) continue;
            var mevcut = sonuc.GetValueOrDefault(pid, ProtokolSinifi.Tamam);
            var yeni = k.Durum switch
            {
                SyncStatus.Failed => ProtokolSinifi.Hatali,
                SyncStatus.Skipped => ProtokolSinifi.Eksik,
                _ => ProtokolSinifi.Tamam,
            };
            sonuc[pid] = (ProtokolSinifi)Math.Max((int)mevcut, (int)yeni);
        }
        return sonuc;
    }

    // Kayit sayisi KAYIT-GUN degil KAYIT basina: ayni kayda iki gun dokunulduysa bir kez.
    private static int TekilSay(IEnumerable<KayitDurumu> kayitlar, SyncStatus durum) => kayitlar
        .Where(k => k.Durum == durum)
        .Select(k => (k.ResourceType, k.PusulaId))
        .Distinct().Count();

    private static List<SebepSatiri> SebepleriTopla(List<KayitDurumu> kayitlar, SyncStatus durum) => kayitlar
        .Where(k => k.Durum == durum)
        .GroupBy(k => SyncLogEntry.ErrorCategory(k.Mesaj))
        .Select(g => new SebepSatiri(
            g.Key.Label, g.Key.Description,
            g.Where(k => k.ProtokolId is not null).Select(k => k.ProtokolId!.Value).Distinct().Count(),
            g.Select(k => (k.ResourceType, k.PusulaId)).Distinct().Count(),
            g.Sum(k => k.Satir),
            g.GroupBy(k => k.ResourceType)
             .Select(t => (t.Key, SyncLogEntry.ResourceTypeLabel(t.Key), t.Select(k => k.PusulaId).Distinct().Count()))
             .OrderByDescending(t => t.Item3)
             .ToList()))
        .OrderByDescending(s => s.Kayit)
        .ToList();

    private void TrendGeometrisiKur()
    {
        const double w = 720, h = 220, padL = 8, padR = 8, padT = 10, padB = 24;
        var n = Ozet.Trend.Count;
        if (n < 2) return;

        TrendMaks = Ozet.Trend.Max(t => t.Toplam);
        var maxV = Math.Max(1, TrendMaks) * 1.15;
        var stepX = (w - padL - padR) / (n - 1);
        double X(int i) => padL + i * stepX;
        double Y(int v) => h - padB - v / maxV * (h - padT - padB);
        static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        string PathFor(Func<int, int> selector) =>
            string.Join(" ", Enumerable.Range(0, n).Select(i => $"{(i == 0 ? "M" : "L")}{F(X(i))},{F(Y(selector(i)))}"));

        TrendTamamPath = PathFor(i => Ozet.Trend[i].Toplam);
        TrendSorunluPath = PathFor(i => Ozet.Trend[i].Eksik + Ozet.Trend[i].Hatali);
        TrendAreaPath = $"{TrendTamamPath} L{F(X(n - 1))},{F(h - padB)} L{F(X(0))},{F(h - padB)} Z";
        TrendLastX = X(n - 1);
        TrendLastY = Y(Ozet.Trend[n - 1].Toplam);

        TrendLabels = new[] { 0, n / 2, n - 1 }.Distinct()
            .Select(i => new TrendLabel(X(i), Ozet.Trend[i].Gun.ToString("dd.MM"),
                i == 0 ? "start" : i == n - 1 ? "end" : "middle"))
            .ToList();
    }
}
