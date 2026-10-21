using System.Globalization;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// SYNCLOG'DAKI ESKI SATIRLARIN PROTOKOL BAGINI DOLDURUR (2026-10-16).
//
// NEDEN VAR: SyncLog.ProtokolId sutunu sonradan eklendi. Yeni kayitlar bagi yazma aninda
// aliyor (her NewEntry fabrikasi ProtokolListItem goruyor), ama o tarihten onceki satirlar
// NULL. Aktivite Akisi gruplamasi bu sutuna gecince, doldurulmamis satirlar hasta
// gruplarina dusmezdi.
//
// NEDEN ACILISTA DEGIL: cozum Pusula'ya sorulan bir sey (ProtokolIdleriniCozAsync) ve
// yuz binlerce satir var. Uygulama acilisini bloklamak, ya da tek seferde hepsini cozmeye
// calismak kabul edilemez. Is parca parca, otomatik turun icinde yuruyor.
//
// NEDEN SAYAC YOK: is DURUM BAZLI -- "ProtokolId IS NULL olan var mi" sorusu her turda
// yeniden soruluyor. Yarim kalan parti, kesilen bir tur, yeniden baslatilan uygulama:
// hicbiri isi bozmuyor, bir sonraki tur kaldigi yerden devam eder. Ilerleme sayaci tutmak
// sadece senkronize edilmesi gereken ikinci bir gercek yaratirdi.
public class ProtokolBagiDoldurmaService(
    SyncLogStore syncLog,
    PusulaRepository repository,
    SettingsStore settings,
    ILogger<ProtokolBagiDoldurmaService> logger)
{
    // Tur basina cozulecek KAYIT sayisi (satir degil -- ayni kaydin onlarca denemesi tek
    // sorguyla hallediliyor).
    //
    // 2026-10-19'da 2.000'den yukseltildi. Ilk dagitimda 623.603 satir doldurulmayi
    // bekliyordu; 2.000'lik partiyle bu, 15 dakikalik tur araliginda yarim gunu buluyordu
    // ve o sure boyunca Aktivite kartlari eski kayitlari gostermiyordu. Ekranin yarim gun
    // eksik sayi gostermesi kabul edilebilir degil.
    //
    // SORGULARI BUYUTMUYOR: ProtokolIdleriniCozAsync zaten kendi icinde 1.000'lik
    // parcalara boluyor (bkz. PusulaRepository.ProtokolCozum). Buradaki sayiyi artirmak
    // tur basina daha COK parca calistirmak demek, daha BUYUK sorgu degil.
    private const int TurBasinaKayit = 10_000;

    public async Task<int> BirParcaDoldurAsync(CancellationToken ct = default)
    {
        var kayitlar = await syncLog.ProtokolBagiEksikKayitlarAsync(TurBasinaKayit, ct);
        if (kayitlar.Count == 0)
        {
            // IS BITTI: kalani SIFIRLA. Yazilmasaydi ekrandaki "doldurma suruyor"
            // uyarisi, is bittikten sonra da son bilinen sayiyla asili kalirdi.
            await settings.SetStringAsync(SettingsStore.ProtokolBagiKalanKey, "0", ct);
            return 0;
        }

        var cozum = new Dictionary<(string, int), int>();

        foreach (var tip in kayitlar.GroupBy(k => k.ResourceType))
        {
            ct.ThrowIfCancellationRequested();
            var idler = tip.Select(k => k.PusulaId).Distinct().ToList();

            Dictionary<int, int> bulunan;
            try
            {
                bulunan = await repository.ProtokolIdleriniCozAsync(tip.Key, idler, ct);
            }
            catch (Exception ex)
            {
                // Pusula'ya erisilemiyorsa doldurma BEKLER. Cozulemedi (-1) yazmak yanlis
                // olurdu: kayit gercekten protokolsuz degil, biz soramadik. -1 kalici bir
                // damga oldugu icin gecici bir ariza yuzunden basilmamali.
                logger.LogWarning(ex,
                    "Protokol bagi doldurma: {Tip} icin cozum alinamadi, bu tur atlandi.", tip.Key);
                continue;
            }

            foreach (var pusulaId in idler)
                cozum[(tip.Key, pusulaId)] = bulunan.TryGetValue(pusulaId, out var pid)
                    ? pid
                    : SyncLogStore.ProtokolCozulemedi;
        }

        if (cozum.Count == 0) return 0;

        var yazilan = await syncLog.ProtokolBagiYazAsync(cozum, ct);

        // KALANI BURADA HESAPLA VE SAKLA. Pahali olan sorgu bu (tarih filtresi yok,
        // tablonun tamamina bakar) -- turda BIR kez calisiyor. Aktivite sayfasi artik
        // kendisi saymiyor, bu degeri okuyor.
        var kalan = await syncLog.ProtokolBagiEksikSayisiAsync(ct);
        await settings.SetStringAsync(
            SettingsStore.ProtokolBagiKalanKey, kalan.ToString(CultureInfo.InvariantCulture), ct);

        logger.LogInformation(
            "Protokol bagi doldurma: {Kayit} kayit cozuldu, {Satir} satir guncellendi. "
            + "Kalan: {Kalan} satir.", cozum.Count, yazilan, kalan);

        return yazilan;
    }
}
