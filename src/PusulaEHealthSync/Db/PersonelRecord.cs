namespace PusulaEHealthSync.Db;

// IK.Personel -> AZ Practitioner FHIR mapping icin kaynak. Kaynak: docs/practitioner-mapping.md.
// KARAR (2026-08-20, canli veriyle dogrulandi): hasta.protokol.DoktorId son 60 gunde
// SADECE PersonelTipiId=1 (Doktor, bkz. Pusula.PersonelTipi lookup) personeli referans
// ediyor -- bu yuzden Practitioner senkronu bu tipe sinirlandirildi.
public class PersonelRecord
{
    public const byte DoktorTipiId = 1;

    public int Id { get; set; }
    public string? Adi { get; set; }
    public string? Soyadi { get; set; }
    public string? TCKimlikNo { get; set; }
    public DateTime? CikisTarihi { get; set; }
    public byte PersonelTipiId { get; set; }
}
