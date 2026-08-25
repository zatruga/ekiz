namespace PusulaEHealthSync.Db;

// Tedavi.ProtokolICD (Sube.Tedavi_ICD ile JOIN) -- bir protokolun ICD-10 tanilari.
// KAYNAK BULUNDU (2026-08-24, kullanici istegi -- bakanlik "Encounter'a da tani ekleyin"
// dedi): ProtokolICD.ICDId int/smallint bir Id, gercek ICD-10 kodu/adi Sube.Tedavi_ICD
// tablosunda -- canli veriyle dogrulandi (Z00.0/E78.4/C50 gibi kodlar AZ terminology
// API'sindeki az-icd-10 CodeSystem'de BIREBIR ayni formatta bulundu).
public class IcdTaniRecord
{
    public int Id { get; set; }
    public int ProtokolId { get; set; }
    public int ICDId { get; set; }
    public required string Kodu { get; set; }
    public string? Adi { get; set; }
    public bool IsBirincilTani { get; set; }
    public bool? IsAnaTani { get; set; }
}
