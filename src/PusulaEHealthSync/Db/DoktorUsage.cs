namespace PusulaEHealthSync.Db;

// Doktorlar sayfasi icin -- "sistem tarafinda doktor" listesi (KULLANICI ISTEGI,
// 2026-08-24: doktor takibi Protokol Detay'dan kaldirilip buraya tasindi). BolumUsage
// ile ayni kalip: IK.Personel'in tamami yerine son N gunde GERCEKTEN protokolu olan
// doktorlar, kullanim sikligina gore.
public class DoktorUsage
{
    public int DoktorId { get; set; }
    public string? Adi { get; set; }
    public string? Soyadi { get; set; }
    public string? TCKimlikNo { get; set; }
    public int Adet { get; set; }

    public string AdiSoyadi => string.Join(" ", new[] { Adi, Soyadi }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
