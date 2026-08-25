namespace PusulaEHealthSync.Db;

// Tedavi.GenelMuayene -- epikriz (discharge summary) kaynagi. Epikriz alani RTF olarak
// tutuluyor (canli veride dogrulandi, 2026-08-20: "{\rtf..." ile basliyor) -- FHIR'e
// gonderilmeden once Mapping.RtfText.ToPlainText ile duz metne cevrilmesi gerekiyor.
//
// KilitDurumuId: son 30 gunluk veriye gore (2026-08-20) "tamamlanma" sinyali olarak
// EpikrizTamamlanmaTarihi ARTIK KULLANILMIYOR (0/22126 kayitta doluydu) -- gercek
// "hekim notu tamamladi/kilitledi" sinyali KilitDurumuId=1. Bkz. CompositionMapper.
public class GenelMuayeneRecord
{
    public int Id { get; set; }
    public int ProtokolId { get; set; }
    public int DoktorId { get; set; }
    public string? Epikriz { get; set; }
    public byte? KilitDurumuId { get; set; }
    public DateTime? EpikrizTamamlanmaTarihi { get; set; }
    public DateTime? MuayeneBaslangicTarihi { get; set; }
    public DateTime? MuayeneBitisTarihi { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? Sikayeti { get; set; }
    public string? Hikayesi { get; set; }
    public string? Soygecmisi { get; set; }
    public string? Bulgulari { get; set; }
    public string? Tani { get; set; }
    public string? TaburcuPlani { get; set; }

    public bool IsLocked => KilitDurumuId == 1;
    public bool HasEpikrizText => !string.IsNullOrWhiteSpace(Epikriz);
}
