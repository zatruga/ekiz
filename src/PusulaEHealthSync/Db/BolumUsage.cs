namespace PusulaEHealthSync.Db;

// Ortak.Bolum'da gercekte kullanilan (protokollerde gecen) bir bolum -- Bolum Eslestirme
// ekraninin kaynak listesi. Tum Ortak.Bolum (440 satir) yerine sadece kullanimda olanlar.
public class BolumUsage
{
    public int BolumId { get; set; }
    public string? Adi { get; set; }
    public int Adet { get; set; }
}
