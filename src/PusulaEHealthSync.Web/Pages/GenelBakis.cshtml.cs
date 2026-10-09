using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;

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
    IMemoryCache onbellek,
    ILogger<GenelBakisModel> logger) : PageModel
{
    // Trend penceresi. Donem bundan uzunsa pencere donemle ayni olur -- boylece TEK
    // protokol cozumu hem donem toplamlarini hem gunluk grafigi besliyor.
    private const int TrendGun = 14;

    // ONBELLEK: protokol cozumu Pusula'ya ~21 sorgu atiyor (olculdu: son 14 gunde 20.819
    // farkli cocuk id). Her F5'te tekrarlamak hem yavas hem gereksiz -- Aktivite Akisi
    // ayni deseni ayni sure ile kullaniyor.
    private static readonly TimeSpan OnbellekOmru = TimeSpan.FromMinutes(2);

    [BindProperty(SupportsGet = true)]
    public string Donem { get; set; } = "7"; // "0" Bugün, "7" Son 7 Gün, "30" Son 30 Gün

    public DateOnly PeriodFromDate { get; private set; }
    public DateOnly PeriodToDate { get; private set; }
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

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

        // -- GONDERIM SAGLIGI: pencerenin kac gununde hic gonderim var --
        int DoluGun, int PencereGun, DateOnly? SonGonderimGunu,

        List<GunNoktasi> Trend,
        List<TurSatiri> Turler,
        List<SebepSatiri> HataSebepleri,
        List<SebepSatiri> EksikSebepleri,
        List<HataSatiri> SonHatalar,
        List<BolumSatiri> Bolumler,

        int IcbariIslem, int IcbariGonderilen, double IcbariYuzde,
        List<IcbariSatiri> IcbariGonderilmeyen,

        bool Kirpildi, bool PusulaHatasi)
    {
        public static readonly BakisOzeti Bos = new(
            0, 0, 0, 0, 0, 0, false, 0, 0, 0,
            0, 0, 0, false,
            0, 0, null,
            [], [], [], [], [], [],
            0, 0, 0, [], false, false);

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
    public record SebepSatiri(string Kategori, string Aciklama, int Protokol, int Kayit, int Satir);

    public record HataSatiri(long SonId, int? ProtokolId, string HastaAdi, string TurEtiketi,
                             string HamMesaj, string AnlasilirMesaj, int Kayit, DateOnly Gun);

    public record BolumSatiri(string Ad, int Protokol, int Tamam, int Eksik, int Hatali, int Pct);

    public record IcbariSatiri(string HastaAdi, int ProtokolId, string HizmetAdi, string Sebep);

    public async Task OnGetAsync(CancellationToken ct)
    {
        var today = Today;
        (PeriodFromDate, PeriodToDate) = Donem switch
        {
            "0" => (today, today),
            "30" => (today.AddDays(-29), today),
            _ => (today.AddDays(-6), today),
        };

        var anahtar = $"genelbakis|{Donem}|{today:yyyy-MM-dd}";
        Ozet = await onbellek.GetOrCreateAsync(anahtar, async giris =>
        {
            var o = await HesaplaAsync(ct);
            // PUSULA PATLADIYSA ONBELLEGE ALMA: eksik bir ozeti iki dakika boyunca
            // dogru gibi gostermek, yavas bir sayfadan kotu.
            giris.AbsoluteExpirationRelativeToNow =
                o.PusulaHatasi ? TimeSpan.FromTicks(1) : OnbellekOmru;
            return o;
        }) ?? BakisOzeti.Bos;

        TrendGeometrisiKur();
    }

    private async Task<BakisOzeti> HesaplaAsync(CancellationToken ct)
    {
        var today = Today;

        // ---- 1) PENCERE: donem ILE trend penceresinin birlesimi ----
        // Trend her zaman en az 14 gun gosteriyor; donem daha uzunsa (30 gun) pencere
        // donemle ayni olur. Boylece TEK protokol cozumu iki isi de besliyor.
        var donemGun = PeriodToDate.DayNumber - PeriodFromDate.DayNumber + 1;
        var pencereGun = Math.Max(TrendGun, donemGun);
        var pencereBas = today.AddDays(-(pencereGun - 1));

        // DIKKAT -- IKI ZAMAN TABANI (bkz. AzTime): Pusula (SQL Server) YEREL saat,
        // SyncLog (SQLite) UTC. 2026-09-09'da bu kafa karisikligi gun sinirini fiilen
        // 04:00'e kaydirmisti; artik yerel degerler Pusula'ya, cevrilenler SyncLog'a.
        var donemBasYerel = PeriodFromDate.ToDateTime(TimeOnly.MinValue);
        var donemBitYerelHaric = PeriodToDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var pencereBasUtc = AzTime.ToUtc(pencereBas.ToDateTime(TimeOnly.MinValue));
        var donemBitUtc = AzTime.ToUtc(donemBitYerelHaric);

        // ---- 2) PROTOKOL BAZLI OZET (pencerenin tamami) ----
        var pencere = await protokolDurum.HesaplaAsync(pencereBasUtc, donemBitUtc, ct);

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
        for (var g = pencereBas; g <= today; g = g.AddDays(1))
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
            .Take(8)
            .ToList();
        var enBuyukBolum = bolumler.Count == 0 ? 0 : bolumler[0].Protokol;
        var bolumSatirlari = bolumler
            .Select(b => new BolumSatiri(b.Ad, b.Protokol, b.Tamam, b.Eksik, b.Hatali,
                enBuyukBolum == 0 ? 0 : (int)Math.Round(b.Protokol * 100.0 / enBuyukBolum)))
            .ToList();

        // ---- 10) PUSULA HACMI: donemde ACILAN protokol ----
        var pusulaHatasi = pencere.PusulaHatasi;
        var acilan = 0;
        var acilanBugun = 0;
        double acilanDegisim = 0;
        var acilanTabanVar = false;
        try
        {
            var donemProtokolListesi = await pusulaRepository.GetProtokolListAsync(
                donemBasYerel, donemBitYerelHaric, null, ct);
            acilan = donemProtokolListesi.Count;

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
        catch (Exception ex)
        {
            pusulaHatasi = true;
            logger.LogWarning(ex, "Genel Bakis: donemde acilan protokol sayisi okunamadi.");
        }

        // ---- 11) ICBARI KAPSAMI ----
        var icbariIslem = 0;
        var icbariGonderilen = 0;
        var icbariListe = new List<IcbariSatiri>();
        try
        {
            // YEREL SAAT, UTC DEGIL. Sorgu p.AcilisTarihi uzerinde calisiyor ve o kolon
            // Pusula'nin YEREL saati. Eski hal buraya AzTime.ToUtc'den gecmis degerleri
            // veriyordu, yani pencere dort saat kayiyordu -- ayni sayfadaki "bugun acilan
            // protokol" sorgusu ise yerel degerle cagriliyordu. Iki sorgu ayni gunu
            // farkli tanimliyordu.
            var icbari = await pusulaRepository.GetIcbariIslemlerAsync(
                donemBasYerel, donemBitYerelHaric, ct);
            icbariIslem = icbari.Count;
            var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
                "Procedure", icbari.Select(i => i.IslemId).ToList(), ct);
            icbariGonderilen = icbari.Count(i => durumlar.GetValueOrDefault(i.IslemId) is { Status: SyncStatus.Success });
            icbariListe = icbari
                .Where(i => durumlar.GetValueOrDefault(i.IslemId) is not { Status: SyncStatus.Success })
                .Select(i =>
                {
                    var son = durumlar.GetValueOrDefault(i.IslemId);
                    var sebep = son is { Status: SyncStatus.Failed }
                        ? SyncLogEntry.ErrorCategory(son.Message).Label
                        : son is { Status: SyncStatus.Skipped }
                            ? SyncLogEntry.ErrorCategory(son.Message).Label
                            : "Henüz gönderilmedi";
                    return new IcbariSatiri(
                        string.IsNullOrWhiteSpace(i.PatientName) ? "-" : i.PatientName,
                        i.ProtokolId, i.HizmetAdi ?? i.IcbariAdi, sebep);
                })
                .Take(25)
                .ToList();
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
            doluGun, pencereGun, sonGonderim,
            trend, turler, hataSebepleri, eksikSebepleri, sonHatalar, bolumSatirlari,
            icbariIslem, icbariGonderilen,
            icbariIslem == 0 ? 0 : Math.Round(icbariGonderilen * 100.0 / icbariIslem, 1),
            icbariListe,
            pencere.Kirpildi, pusulaHatasi);
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
            g.Sum(k => k.Satir)))
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
