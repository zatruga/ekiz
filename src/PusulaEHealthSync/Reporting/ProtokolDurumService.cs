using Microsoft.Extensions.Logging;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Reporting;

// ============ PROTOKOL BAZLI DURUM OZETI (2026-10-09) ============
//
// KULLANICI ISTEGI: "genel bakis alanini bir kontrol eder misin, burada ana kriter
// PROTOKOL olmali, sayilar protokol ozelinde listelemeli, altinda satir sayisi olabilir."
//
// NEDEN AYRI BIR SERVIS: ayni soruyu ("hangi protokol ne durumda") bugune kadar UC yer
// birbirinden bagimsiz cevapliyordu -- gun sonu raporu (GunSonuRaporService), Aktivite
// Akisi ve Genel Bakis. Ucu de ayni kurali ("en kotu durum kazanir") elle tekrar
// yaziyordu. Bu projede ayni hesabin iki yerde durmasinin bedeli zaten bir kez odendi:
// Protokol Detay ile Aktivite ayri gonderim yolu kullandigi icin 2026-09-15'te
// birbirinden ayrismislardi (cozum: TekilGonderimService). Burada ucuncu kopyayi
// yazmamak icin hesap tek yere alindi.
//
// AKTIVITE VE GUN SONU RAPORU SIMDILIK KENDI HESABINI KORUYOR -- ikisinin de bu ozetin
// vermedigi ek ihtiyaclari var (Aktivite: hasta gruplama + sayfalama + protokol
// detaylari; rapor: satir bazli hatali kalem listesi). Onlari da buraya tasimak bu
// isteğin kapsami disinda; birlestirilecekse bu dosya dogru yer.
//
// SALT OKUNUR: Pusula'ya yalnizca protokol numarasi cozmek icin SELECT atilir.
public class ProtokolDurumService(
    SyncLogStore syncLog,
    PusulaRepository repository,
    ILogger<ProtokolDurumService> logger)
{
    // Kayit-gun grubu tavani. Olculdu (sunucu gunlugu, son 14 gun): 38.868 grup.
    // Tavan bunun ~5 kati -- tekrar gonderim duzeltmesi (c761e0f) dagitildiktan sonra
    // satir sayisi 10 kat dusecegi icin bu rahat bir pay. Asilirsa cagiran taraf bunu
    // EKRANDA soylemek zorunda (Kirpildi), eksik bir toplami tam gibi gostermemek icin.
    public const int Tavan = 200_000;

    public async Task<ProtokolDurumOzeti> HesaplaAsync(
        DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        var ham = await syncLog.GetKayitGunOzetleriAsync(fromUtc, toUtcExclusive, Tavan, ct);
        if (ham.Count == 0) return ProtokolDurumOzeti.Bos;
        var kirpildi = ham.Count >= Tavan;

        // ---- 1) COCUK KAYIT -> PROTOKOL ----
        // SyncLog'da protokol bagi YOK: her kaynak tipi kendi id uzayini kullaniyor
        // (Observation -> LabaratuarSonucId, Procedure -> ProtokolIslem.Id ...).
        // Numarayi Pusula'ya sorarak cozuyoruz (bkz. PusulaRepository.ProtokolCozum).
        //
        // PUSULA ERISILEMEZSE SAYFA YINE ACILIR: cozulemeyen kayitlar "protokole
        // baglanamadi" kovasinda GORUNUR kalir, sessizce dusurulmez. Gonderim gunlugu,
        // tam da Pusula'ya erisilemedigi anda en cok ihtiyac duyulan sey.
        var esleme = new Dictionary<(string, int), int>();
        var pusulaHatasi = false;
        foreach (var tip in ham.GroupBy(k => k.ResourceType))
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
                logger.LogWarning(ex,
                    "Protokol durum ozeti: {Tip} icin protokol numaralari cozulemedi, "
                    + "bu turun kayitlari protokolsuz sayilacak.", tip.Key);
            }
        }

        // ---- 2) PATIENT KAYITLARINI PROTOKOLE KAT ----
        //
        // NEDEN GEREKLI: Patient'in PusulaId'si HastaId'dir, protokol degil -- hasta
        // protokoller arasi paylasildigi icin ProtokolIdleriniCozAsync onu bilerek
        // cozmez. Ama ProtocolFullSyncService her protokol gonderiminde bir Patient
        // satiri da yazar; yani bu kayitlar aslinda bir protokolun parcasi.
        //
        // KATMAZSAK NE OLUR: hastasi gonderilemeyen bir protokol ekranda "Tamam"
        // gorunur. Olculdu (sunucu, son 7 gun): 5 hatali + 17 atlanan Patient kaydi --
        // yani 22 protokol sorunsuz gibi sayilirdi, oysa hicbiri TRƏS'e tam gitmedi.
        //
        // AYNI HASTANIN ARALIKTA BIRDEN COK PROTOKOLU VARSA en son dokunulan seciliyor.
        // Kesin bag SyncLog'da yok; Patient gonderimi protokol gonderiminin ICINDEN
        // tetiklendigi icin pratikte dogru olan da bu. Aktivite Akisi ayni kurali
        // kullaniyor -- iki ekran ayni kayda ayni protokolu versin diye.
        //
        // PRACTITIONER BILEREK DISARIDA: bir doktor ayni gun onlarca protokolde yer alir,
        // kaydi tek bir protokole yazmak uydurma olurdu. Protokolsuz kaliyor ve ekranda
        // "protokole baglanamayan" olarak GORUNUYOR.
        var protokolIdleri = esleme.Values.Distinct().ToList();
        var protokoller = new Dictionary<int, ProtokolListItem>();
        if (protokolIdleri.Count > 0)
        {
            try
            {
                protokoller = await repository.GetProtokollerByIdsAsync(protokolIdleri, ct);
            }
            catch (Exception ex)
            {
                pusulaHatasi = true;
                logger.LogWarning(ex, "Protokol durum ozeti: protokol bilgileri okunamadi.");
            }
        }

        var hastaninProtokolu = new Dictionary<int, (int ProtokolId, DateOnly Gun)>();
        foreach (var k in ham)
        {
            if (!esleme.TryGetValue((k.ResourceType, k.PusulaId), out var pid)) continue;
            if (!protokoller.TryGetValue(pid, out var pr) || pr.HastaId == 0) continue;
            if (!hastaninProtokolu.TryGetValue(pr.HastaId, out var mevcut) || k.Gun > mevcut.Gun)
                hastaninProtokolu[pr.HastaId] = (pid, k.Gun);
        }

        // ---- 3) KAYITLARA PROTOKOL NUMARASI YAZ ----
        var kayitlar = ham.Select(k =>
        {
            int? pid = esleme.TryGetValue((k.ResourceType, k.PusulaId), out var p) ? p
                     : k.ResourceType == "Patient"
                       && hastaninProtokolu.TryGetValue(k.PusulaId, out var hp) ? hp.ProtokolId
                     : null;
            return new KayitDurumu(k.SonId, k.ResourceType, k.PusulaId, k.Gun, k.Durum,
                                   k.SatirSayisi, k.Mesaj, pid);
        }).ToList();

        // ---- 4) PROTOKOL BAZINA TOPLA ----
        // "EN KOTU DURUM KAZANIR": bir protokolun kayitlarindan biri hata aldiysa
        // protokol HATALI, hatasi yok ama eksigi varsa EKSIK, ikisi de yoksa TAMAM.
        // Sira onemli -- hata eksigi ezer, cunku once hatanin gorulmesi gerekiyor.
        // Ayni kural: GunSonuRaporService, Aktivite Akisi, SyncLogStore'daki kayit
        // sayimlari. Dort yerde ayni cevap.
        var kovalar = new Dictionary<int, (int B, int E, int H, int Satir)>();
        foreach (var k in kayitlar)
        {
            if (k.ProtokolId is not { } pid) continue;
            var c = kovalar.GetValueOrDefault(pid);
            kovalar[pid] = (
                c.B + (k.Durum == SyncStatus.Success ? 1 : 0),
                c.E + (k.Durum == SyncStatus.Skipped ? 1 : 0),
                c.H + (k.Durum == SyncStatus.Failed ? 1 : 0),
                c.Satir + k.Satir);
        }

        var protokolSatirlari = kovalar
            .Select(kv => new ProtokolSatiri(
                kv.Key,
                kv.Value.H > 0 ? ProtokolSinifi.Hatali
                    : kv.Value.E > 0 ? ProtokolSinifi.Eksik
                    : ProtokolSinifi.Tamam,
                kv.Value.B, kv.Value.E, kv.Value.H, kv.Value.Satir))
            .ToList();

        return new ProtokolDurumOzeti(kayitlar, protokolSatirlari, protokoller, kirpildi, pusulaHatasi);
    }
}

// Bir protokolun TEK bir kaydinin, TEK bir gundeki durumu. ProtokolId null ise kayit
// protokole baglanamadi (Practitioner, ya da Pusula'da karsiligi silinmis bir cocuk
// kayit) -- cagiran taraf bu kayitlari EKRANDA ayrica gosterir, sessizce dusurmez.
public record KayitDurumu(long SonId, string ResourceType, int PusulaId, DateOnly Gun,
                          SyncStatus Durum, int Satir, string? Mesaj, int? ProtokolId);

public enum ProtokolSinifi { Tamam, Eksik, Hatali }

public record ProtokolSatiri(int ProtokolId, ProtokolSinifi Sinif,
                             int KayitBasarili, int KayitEksik, int KayitHatali, int Satir)
{
    public int Kayit => KayitBasarili + KayitEksik + KayitHatali;
}

// UC BIRIM, TEK OZET: protokol (ana olcu), kayit (alt olcu), satir (en alt olcu).
// Ekranin her sayisi bu uclusunden birine ait oldugunu SOYLEMEK zorunda -- birimini
// soylemeyen bir sayi, bu projede yanlis sayidan daha cok kafa karistirdi.
public record ProtokolDurumOzeti(
    List<KayitDurumu> Kayitlar,
    List<ProtokolSatiri> Protokoller,
    Dictionary<int, ProtokolListItem> ProtokolBilgileri,
    bool Kirpildi,
    bool PusulaHatasi)
{
    public static readonly ProtokolDurumOzeti Bos = new([], [], [], false, false);

    public int ProtokolToplam => Protokoller.Count;
    public int ProtokolTamam => Protokoller.Count(p => p.Sinif == ProtokolSinifi.Tamam);
    public int ProtokolEksik => Protokoller.Count(p => p.Sinif == ProtokolSinifi.Eksik);
    public int ProtokolHatali => Protokoller.Count(p => p.Sinif == ProtokolSinifi.Hatali);

    // Kayit sayilari KAYIT-GUN degil KAYIT basina: ayni kayda iki gun dokunulduysa
    // bir kez sayilir. Gunluk trend icin Kayitlar listesi oldugu gibi kullanilir.
    private HashSet<(string, int)>? _tekil;
    private HashSet<(string, int)> Tekil => _tekil ??= [.. Kayitlar.Select(k => (k.ResourceType, k.PusulaId))];
    public int KayitToplam => Tekil.Count;
    public int SatirToplam => Kayitlar.Sum(k => k.Satir);

    // Protokole baglanamayan kayitlar -- cogu Practitioner (bir doktor onlarca
    // protokolde yer alir, tek protokole yazmak uydurma olurdu).
    public int ProtokolsuzKayit => Kayitlar
        .Where(k => k.ProtokolId is null)
        .Select(k => (k.ResourceType, k.PusulaId))
        .Distinct().Count();

    public double TamamYuzde => ProtokolToplam == 0
        ? 0 : Math.Round(ProtokolTamam * 100.0 / ProtokolToplam, 1);
}
