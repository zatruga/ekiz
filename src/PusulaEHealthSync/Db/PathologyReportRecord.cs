namespace PusulaEHealthSync.Db;

// EMR.Pathology.Result -- ReportState=4 ("Onaylanmis", canli veride dogrulandi 2026-09-02:
// ApprovedDate 1:1 dolu, RejectedDate hep bos). Bu, Pusula'nin kendi LIS.Patoloji* semasindan
// FARKLI, ayri (ucuncu parti) bir patoloji sistemi -- LIS.Patoloji* tablolari canli veride
// tamamen bos (0 satir) cikti, gercek veri EMR.Pathology semasinda.
//
// PatientId/VisitId dogrudan hasta.hasta.Id/hasta.protokol.Id ile ayni (canli veride
// dogrulandi) -- ayri bir kimlik eslestirmesi gerekmiyor.
//
// DUZELTME (2026-09-02, canli test -- kullanici: "procedürde patoloji yok ama patolojide
// hizmeti gözüküyor"): ILK TASARIM [EMR.Pathology].[Order_Procedure].ProcedureId'yi
// Hasta.ProtokolIslem.Id sanıyordu -- YANLIŞ CIKTI ("Beta-hCG", "ESWT" gibi alakasiz hizmetler
// "patoloji hizmeti" olarak gorunuyordu). Iki ara-cozum (PatolojiTipiId filtresi, sonra protokol
// capinda "tek temsilci Islem" secimi) denendikten sonra KESIN koprus bulundu: ProcedureId
// aslinda Ortak.Hizmet.Id (HizmetId) imis -- ProtokolIslem'le hic ilgisi yok. Dogru kolon
// [Order_Procedure].ProcessId (canli veride dogrulandi, orn. ProcessId=9288039 -> gercek bir
// ProtokolIslem.Id, HizmetId=106525 "Yumşaq toxuma, debridman, patoloji müayinə"). ProcessId
// (ve ReportNo) Pusula'nin kendi faturalama adimi tamamlaninca doluyor -- islenmemis
// siparislerde ProcessId=0/ReportNo=NULL kaliyor. Artik Radyoloji kadar kesin: her Result
// KENDI Order_Procedure satirlarindan (OrderId=Result.Id) dogru ProtokolIslem'ine bagli,
// protokol capinda tahmin yapmaya gerek yok. Bkz. PusulaRepository.GetPathologyReportsByProtokolIdAsync.
public class PathologyReportRecord
{
    public int ResultId { get; set; }
    public int? ProtokolIslemId { get; set; }
    public int? HizmetId { get; set; }
    public string? HizmetAdi { get; set; }
    public string? Document { get; set; }
    public DateTime? RequestedAt { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public int? ApprovedById { get; set; }

    // AZ DiagnosticReport'ta ZORUNLU procedure-code extension'i icin -- Lab/Islem/Radyoloji
    // ile ayni Icbari Sigorta Fiyat Listesi koprusu.
    public string? IcbariKodu { get; set; }
    public string? IcbariAdi { get; set; }
}
