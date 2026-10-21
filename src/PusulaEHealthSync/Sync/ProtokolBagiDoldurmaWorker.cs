using Microsoft.Extensions.Hosting;

namespace PusulaEHealthSync.Sync;

// PROTOKOL BAGI DOLDURMANIN KENDI DONGUSU (2026-10-21).
//
// ONCE AutoSyncWorker'in turu icindeydi -- gonderim turu BITTIKTEN SONRA cagriliyordu.
// Sunucu verisinde olculdu: 19.10'daki dagitimdan iki gun sonra doldurulmayi bekleyen
// satir sayisi 623.603'ten 623.611'e CIKMISTI, yani doldurma bir kez bile calismamisti.
// "Kalan satir" ayari hic yazilmamisti -- calissaydi bitince bile "0" yazardi.
//
// SEBEP: parti boyutu 5.000 protokol, sistem gunde ~3.000 protokol gonderebiliyor. Tek
// bir tur bir bucuk gunu asiyor ve her dagitim uygulamayi yeniden baslattigi icin tur
// HIC BITMIYORDU (son tur izi 14.10'da kalmis). Turun arkasina konmus is, turun
// bitmesine rehin kaliyordu. Sonuc: Aktivite kartlari fiilen yalnizca son birkac gunu
// sayiyordu; uyari bandi da "kalan" ayari hic yazilmadigi icin 0 okuyup susuyordu.
//
// AYRI DONGU BU YUZDEN: is bakanlik sistemine HICBIR SEY yazmiyor (Pusula'dan okur,
// kendi SQLite gunlugumuzu gunceller), dolayisiyla gonderim anahtarina ya da turun
// temposuna baglanmasi icin bir sebep yok. Parca basina 10.000 kayit (bkz.
// ProtokolBagiDoldurmaService); is varken parcalar arasi kisa bekleme, is bitince seyrek
// yoklama.
public class ProtokolBagiDoldurmaWorker(
    ProtokolBagiDoldurmaService doldurma,
    ILogger<ProtokolBagiDoldurmaWorker> logger) : BackgroundService
{
    // Is varken: Pusula'ya ve SQLite'a nefes aldiran kisa ara. Gonderim dongusu ayni
    // veritabanina yaziyor; parcalar arasindaki bu bosluk onun kilit beklemesini kisa tutar.
    private static readonly TimeSpan IsVarkenAra = TimeSpan.FromSeconds(20);

    // Is bitince: yeni satirlar bagi yazma aninda aldigi icin normalde hic is cikmaz;
    // bu yoklama yalnizca istisnai durumlar icin (ornegin Pusula kesintisinde atlanan tip).
    private static readonly TimeSpan BosAra = TimeSpan.FromMinutes(30);

    // Pusula erisilemezken her 20 sn'de bir denemesin.
    private static readonly TimeSpan HataAra = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Acilista web tarafi ayaga kalksin (AutoSyncWorker ile ayni gerekce).
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan ara;
            try
            {
                var yazilan = await doldurma.BirParcaDoldurAsync(stoppingToken);
                ara = yazilan > 0 ? IsVarkenAra : BosAra;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // YARDIMCI bir is: patlarsa uygulamayi dusurmemeli, bir sure sonra yeniden dener.
                logger.LogWarning(ex, "Protokol bagi doldurma parcasi calisamadi.");
                ara = HataAra;
            }

            try { await Task.Delay(ara, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
