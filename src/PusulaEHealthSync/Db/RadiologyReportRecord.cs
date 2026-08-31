namespace PusulaEHealthSync.Db;

// RIS.TetkikIslem -- Lab/Islem'deki State=6 kuraliyla AYNI ("onaylanmis/kesinlesmis").
//
// DUZELTME (2026-08-31, canli hata -- kullanici: MRT raporunda "Bel f?q?r?l?rinin..." gibi
// "?" isaretleri): duz metin Rapor kolonu ILK BASTA kullanildi ("zaten temiz metin" sanildi,
// canli SQL ornek satirinda -- USG raporu -- dogru gorunmustu), ama bu SATIRA OZGU bir
// tesadufti -- GENELDE Rapor kolonu Pusula'nin KENDI RTF->duz metin cikarimindan geliyor ve
// bu cikarim Azerice/Turkce ozel karakterleri (codepage 1254 ANSI fallback) bazen "?" ile
// degistiriyor. Tedavi.GenelMuayene.Epikriz icin AYNI sorun zaten cozulmustu (bkz. RtfText.cs)
// -- o cozum RAW RTF'i (RaporRtf) SAKLAYIP donusumu MAPPER katmaninda RtfText.ToPlainText ile
// yapiyor. Ayni kalip burada da uygulaniyor: Rapor alani artik HAM RTF (RaporRtf sutunu, o
// bos ise duz Rapor'a fallback) tutuyor, RadiologyReportMapper kendi RtfText.ToPlainText
// cagrisini yapiyor.
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
