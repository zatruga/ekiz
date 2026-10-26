using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Reporting;

// ============ ICBARI SIGORTA GONDERIM KAPSAMI (2026-10-26) ============
//
// KULLANICI: "icbari barini hasta listesi olarak degil daha cok rakamlara odaklanalim ve
// tiklayinca filtre edilmis sayfaya gidelim. buradaki istatistigi arttiralim -- bu onemli,
// asil para kaynagi, kazanc kapisi burasi."
//
// TEK HESAP, IKI EKRAN: Genel Bakis istatistigi (IcbariOzeti) ve Icbari Islemler sayfasi
// (Kalemler) ayni listeden turuyor. Ayri hesaplansalardi kartta "312 hatali" yazip
// tiklayinca 309 satir gelebilirdi -- bu projede o tur ayrismanin bedeli birkac kez odendi.
//
// OLCU ISLEM: faturalama kalemi islemin kendisi (Hasta.ProtokolIslem), protokol degil.
// Kume PusulaRepository.GetIcbariIslemlerAsync -- gonderimle BIREBIR ayni eslesme kurallari.
//
// SALT OKUNUR: Pusula'ya yalnizca SELECT.
public class IcbariKapsamService(PusulaRepository repository, SyncLogStore syncLog, SettingsStore settings)
{
    public enum Durum { Gitti, Eksik, Hatali, Denenmedi }

    public record Kalem(IcbariIslemRecord Islem, Durum Durum, string Sebep, string? Mesaj, DateTime? SonDenemeUtc);

    // PROTOKOL BIRIMI (2026-10-26, kullanici: "ana gozuken veriler protokol bazli olsun,
    // altinda veri sayilari olsun"). Bir protokolun icbari durumu, islemlerinden:
    //   hepsi gitti -> Gitti; degilse EN KOTU KAZANIR: Hatali > Eksik > Denenmedi.
    // Aktivite Akisi'ndaki protokol sinifiyla ayni kural -- iki ekran ayni protokole ayni
    // sinifi versin.
    public record ProtokolKalemi(int ProtokolId, string? Hasta, List<Kalem> Kalemler)
    {
        public Durum Durum { get; } = ProtokolDurumu(Kalemler);
        public int Gitti => Kalemler.Count(k => k.Durum == Durum.Gitti);
        public int Gitmedi => Kalemler.Count - Gitti;
        public DateTime? SonDenemeUtc => Kalemler.Max(k => k.SonDenemeUtc);
    }

    public static Durum ProtokolDurumu(IEnumerable<Kalem> kalemler)
    {
        var liste = kalemler as IReadOnlyCollection<Kalem> ?? kalemler.ToList();
        if (liste.All(k => k.Durum == Durum.Gitti)) return Durum.Gitti;
        if (liste.Any(k => k.Durum == Durum.Hatali)) return Durum.Hatali;
        if (liste.Any(k => k.Durum == Durum.Eksik)) return Durum.Eksik;
        return Durum.Denenmedi;
    }

    public static List<ProtokolKalemi> Protokollere(IEnumerable<Kalem> kalemler) => kalemler
        .GroupBy(k => k.Islem.ProtokolId)
        .Select(g => new ProtokolKalemi(g.Key, g.Select(k => k.Islem.PatientName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                                        g.ToList()))
        .ToList();

    // Sebep ve hizmet satirlari: ana sayi PROTOKOL, alt sayi ISLEM.
    public record SebepSatiri(string Sebep, Durum Durum, int Protokol, int Islem);
    public record HizmetSatiri(string Hizmet, int Toplam, int Gitti, int ProtokolGitmedi)
    {
        public int Gitmedi => Toplam - Gitti;
        public double GittiYuzde => Toplam == 0 ? 0 : Math.Round(Gitti * 100.0 / Toplam, 1);
    }

    // Sayim cifti: ana olcu PROTOKOL (o siniftaki protokoller), alt olcu ISLEM (o durumdaki
    // islemler -- protokol sinifindan bagimsiz). Ornek: "Gonderildi: 2.000 protokolun tum
    // icbari islemleri gitti · toplam 49.639 islem gonderildi" -- kismen giden protokolun
    // giden islemleri de islem sayisina girer.
    public record Sayi(int Protokol, int Islem);

    public record IcbariOzeti(
        Sayi Toplam, Sayi Gitti, Sayi Gitmedi, Sayi Eksik, Sayi Hatali, Sayi Denenmedi,
        List<SebepSatiri> Sebepler, List<HizmetSatiri> Hizmetler, int HizmetCesidi)
    {
        public static readonly IcbariOzeti Bos =
            new(new(0, 0), new(0, 0), new(0, 0), new(0, 0), new(0, 0), new(0, 0), [], [], 0);
        public double ProtokolYuzde(int n) => Toplam.Protokol == 0 ? 0 : Math.Round(n * 100.0 / Toplam.Protokol, 1);
        public double IslemYuzde(int n) => Toplam.Islem == 0 ? 0 : Math.Round(n * 100.0 / Toplam.Islem, 1);
    }

    public const string HenuzGonderilmedi = "Henüz gönderilmedi";

    // fromLocal/toLocalExclusive: protokolun Pusula'daki ACILIS tarihi (yerel saat).
    public async Task<List<Kalem>> KalemlerAsync(
        DateTime fromLocal, DateTime toLocalExclusive, string ortam, CancellationToken ct = default)
    {
        var islemler = await repository.GetIcbariIslemlerAsync(fromLocal, toLocalExclusive, ct);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "Procedure", islemler.Select(i => i.IslemId).ToList(), ct, govdeleriGetir: false, ortamZorla: ortam);

        static Durum DurumBul(SyncLogEntry? son) => son switch
        {
            null => Durum.Denenmedi,
            // Silinmis (Delete) son kayit "gitti" sayilmaz -- TRƏS'te artik yok.
            { Status: SyncStatus.Success, Operation: SyncOperation.Delete } => Durum.Denenmedi,
            { Status: SyncStatus.Success } => Durum.Gitti,
            { Status: SyncStatus.Skipped } => Durum.Eksik,
            _ => Durum.Hatali,
        };

        // DENENMEMIS ISLEMLERIN SEBEBI (2026-10-26, kullanici: "eksik kalanlari tikliyorum,
        // 'henuz gonderilmedi' verisi geliyor"). "Henuz gonderilmedi" bir sebep degil --
        // cogu, gonderim kurali geregi BEKLIYOR (hasta yatiyor, protokol acik). Sebep
        // gonderimin KENDI kuralindan (PendingWorkService.IsEligible) cikariliyor; ekran ile
        // gonderim ayri kural kullanirsa ekranda "kuyrukta" yazan protokol gitmez.
        var denenmemisProtokoller = islemler
            .Where(i => DurumBul(durumlar.GetValueOrDefault(i.IslemId)) == Durum.Denenmedi)
            .Select(i => i.ProtokolId).Distinct().ToList();
        var sebepler = await DenenmemeSebepleriAsync(denenmemisProtokoller, ortam, ct);

        return islemler.Select(i =>
        {
            var son = durumlar.GetValueOrDefault(i.IslemId);
            var durum = DurumBul(son);
            var (sebep, mesaj) = durum switch
            {
                Durum.Gitti => ("Gönderildi", (string?)null),
                Durum.Denenmedi => sebepler.GetValueOrDefault(i.ProtokolId, (HenuzGonderilmedi, null)),
                _ => (SyncLogEntry.ErrorCategory(son!.Message).Label, son!.Message),
            };
            return new Kalem(i, durum, sebep, mesaj, son?.CreatedAtUtc);
        }).ToList();
    }

    // Protokol -> (sabit kategori, ayrintili metin). Kategori sabit ki gruplanabilsin
    // ("12 gundur acik" ile "13 gundur acik" ayri satir olmasin); ayrinti ipucunda.
    private async Task<Dictionary<int, (string Sebep, string? Ayrinti)>> DenenmemeSebepleriAsync(
        List<int> protokolIdleri, string ortam, CancellationToken ct)
    {
        var sonuc = new Dictionary<int, (string, string?)>();
        if (protokolIdleri.Count == 0) return sonuc;

        var protokoller = await repository.GetProtokollerByIdsAsync(protokolIdleri, ct);
        var openAfterDays = await settings.GetIntAsync(
            SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);
        var minYas = await settings.GetIntAsync(
            SettingsStore.MinProtokolYasiGunKey, SettingsStore.MinProtokolYasiGunDefault, ct);
        var yatanTavan = await settings.GetIntAsync(
            SettingsStore.YatanMaxOpenDaysKey, SettingsStore.YatanMaxOpenDaysDefault, ct);
        var yatanOtomatik = await settings.GetBoolAsync(
            SettingsStore.AutoSendYatanEnabledKey, SettingsStore.AutoSendYatanEnabledDefault, ct);
        var tabanHam = await settings.GetStringAsync(SettingsStore.SendFloorDateKey, "", ct);
        DateOnly? taban = DateOnly.TryParseExact(tabanHam, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? t : null;

        // MUAYENE GITMEDIYSE ISLEMLER HIC DENENMEZ: Procedure zincirin icinde, muayeneden
        // sonra gidiyor. Uygun gorunen ama denenmemis bir protokolde sebep cogu zaman bu
        // (ornegin hastanin FIN'i bos) -- "kuyrukta" demek yanlis olurdu.
        var muayeneler = await syncLog.GetLatestByPusulaIdsAsync(
            "Encounter", protokolIdleri, ct, govdeleriGetir: false, ortamZorla: ortam);

        foreach (var (pid, p) in protokoller)
        {
            if (taban is { } tb && p.AcilisTarihi is { } acilis && DateOnly.FromDateTime(acilis) < tb)
            {
                sonuc[pid] = ("Kapsam dışı — devreye alımdan önce açılmış", null);
                continue;
            }
            var (uygun, neden) = PendingWorkService.IsEligible(p, openAfterDays, minYas, yatanTavan);
            if (uygun && muayeneler.GetValueOrDefault(pid) is { Status: not SyncStatus.Success } muayene)
                sonuc[pid] = ($"Muayene gönderilemedi — {SyncLogEntry.ErrorCategory(muayene.Message).Label}", muayene.Message);
            else if (uygun)
                sonuc[pid] = !yatanOtomatik && p.GelisTipiId == "Y"
                    ? ("Yatan — otomatik dışı, elle gönderilecek", PendingWorkService.YatanOtomatikDisiSebep)
                    : ("Kuyrukta — sıradaki turda gönderilecek", null);
            else if (p.GelisTipiId == "Y" && p.TaburcuTarihi is null && p.KapanisTarihi is null)
                sonuc[pid] = ("Sırası gelmedi — hasta yatıyor", neden);
            else if (p.KapanisTarihi is null && p.GelisTipiId != "Y")
                sonuc[pid] = ($"Sırası gelmedi — protokol açık ({openAfterDays} gün beklenir)", neden);
            else
                sonuc[pid] = ("Sırası gelmedi — bugün kapandı", neden);
        }
        return sonuc;
    }

    public static IcbariOzeti Ozetle(List<Kalem> kalemler, int hizmetTavan = 10)
    {
        if (kalemler.Count == 0) return IcbariOzeti.Bos;

        var protokoller = Protokollere(kalemler);
        Sayi Sinif(Durum d) => new(protokoller.Count(p => p.Durum == d), kalemler.Count(k => k.Durum == d));

        var hizmetler = kalemler
            .GroupBy(k => HizmetAdi(k.Islem))
            .Select(g => new HizmetSatiri(g.Key, g.Count(),
                g.Count(k => k.Durum == Durum.Gitti),
                g.Where(k => k.Durum != Durum.Gitti).Select(k => k.Islem.ProtokolId).Distinct().Count()))
            .ToList();

        return new IcbariOzeti(
            new(protokoller.Count, kalemler.Count),
            Sinif(Durum.Gitti),
            new(protokoller.Count(p => p.Durum != Durum.Gitti), kalemler.Count(k => k.Durum != Durum.Gitti)),
            Sinif(Durum.Eksik),
            Sinif(Durum.Hatali),
            Sinif(Durum.Denenmedi),
            kalemler.Where(k => k.Durum != Durum.Gitti)
                .GroupBy(k => (k.Sebep, k.Durum))
                .Select(g => new SebepSatiri(g.Key.Sebep, g.Key.Durum,
                    g.Select(k => k.Islem.ProtokolId).Distinct().Count(), g.Count()))
                .OrderByDescending(s => s.Protokol)
                .ToList(),
            // En cok GITMEYEN hizmet once (protokol sayisina gore): "neyi duzeltirsem en cok
            // kazandiririm" sorusunun cevabi.
            hizmetler.Where(h => h.Gitmedi > 0).OrderByDescending(h => h.ProtokolGitmedi).Take(hizmetTavan).ToList(),
            hizmetler.Count);
    }

    public static string HizmetAdi(IcbariIslemRecord i) =>
        string.IsNullOrWhiteSpace(i.HizmetAdi) ? i.IcbariAdi : i.HizmetAdi!;

    public static string DurumAnahtari(Durum d) => d switch
    {
        Durum.Gitti => "gitti",
        Durum.Eksik => "eksik",
        Durum.Hatali => "hatali",
        _ => "denenmedi",
    };
}
