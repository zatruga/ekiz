namespace PusulaEHealthSync.Db;

// RIS.TetkikIslem -- Lab/Islem'deki State=6 kuraliyla AYNI ("onaylanmis/kesinlesmis").
// Rapor kolonu (duz metin) canli veriyle dogrulandi (2026-08-31): State=6 olan satirlarda
// dolu, Azerice serbest metin radyoloji raporu. RaporRtf (bicimlendirilmis surum) KASITLI
// olarak okunmuyor -- DiagnosticReport.conclusion duz metin bekliyor, RTF kontrol karakterleri
// (bkz. canli ornek) hicbir FHIR alanina uymuyor.
public class RadiologyReportRecord
{
    public int TetkikIslemId { get; set; }
    public int ProtokolIslemId { get; set; }
    public int HizmetId { get; set; }
    public string? HizmetAdi { get; set; }
    public string? Rapor { get; set; }
    public DateTime? CalismaTarihi { get; set; }
    public DateTime? OnaylanmaTarihi { get; set; }
    public int? RaporuOnaylayanDoktorId { get; set; }

    // AZ DiagnosticReport'ta ZORUNLU procedure-code extension'i icin -- Lab/Islem ile ayni
    // Icbari Sigorta Fiyat Listesi koprusu (bkz. PusulaRepository.GetIslemlerByProtokolIdAsync).
    public string? IcbariKodu { get; set; }
    public string? IcbariAdi { get; set; }
}
