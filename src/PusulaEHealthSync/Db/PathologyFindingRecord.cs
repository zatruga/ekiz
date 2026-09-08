namespace PusulaEHealthSync.Db;

// [EMR.Pathology].[EPulse] -- bir patoloji raporunun KODLANMIS bulgusu (topografya +
// morfoloji). PathologyReportRecord (= [EMR.Pathology].[Result], serbest metin rapor) ile
// PatolojiIstekId uzerinden baglanir: EPulse.PatolojiIstekId = Result.Id (canli veride
// dogrulandi 2026-09-08 -- 52.490/52.490 satir eslesiyor, tam kapsama).
//
// NEDEN AYRI BIR KAYIT: v1 sadece DiagnosticReport'u (serbest metin) gonderiyordu, cunku
// ICD-O-3 kodlamasi icin guvenilir bir kaynak bulunamamisti. Bu YANLIS cikti -- kodlanmis
// veri Result'ta degil EPulse'ta duruyormus (bkz. asagidaki iki alan).
//
// MORFOLOJI: MorfolojiKoduCode ZATEN ICD-O-3 formatinda (orn. "8523/3") -- AZ CodeSystem'i
// (http://fhir.az/CodeSystem/az-icd-o-3) BIREBIR ayni bicimi kullaniyor (IG'den dogrulandi,
// orn. "8500/3" = "Infiltrasiya eden axacaq karsinomasi, EGO"). Cevirim GEREKMIYOR, dogrudan
// gecer. (Result.ICDOs kolonu tam bu is icin duruyor gibi gorunuyor ama canli veride 0 satir
// dolu -- Skrs.YerlesimYeri.TopografikKodu gibi hic doldurulmamis bir kolon.)
//
// TOPOGRAFYA: YerlesimYeriCode ise SKRS IC kodu (orn. "1212"), ICD-O-3 DEGIL -- ceviri icin
// bkz. Mapping/PathologyTopographyMap.cs.
//
// TEKRARLAR: EPulse blok/preparat basina satir tutar, bakanlik bildirim alanlari raporun TEK
// tanisini her blokta yineler. Canli veride olculdu (2026-09-08): 6.571 gonderilebilir
// raporun BENZERSIZ (topografya, morfoloji) cifti sayisi da tam 6.571 -- yani her raporun
// tek bir farkli bulgusu var, 11.607 ham satirin fazlasi saf tekrar. Bu yuzden sorgu
// (YerlesimYeriCode, MorfolojiKoduCode) uzerinden TEKILLESTIRIR; yoksa ayni Observation
// 12 kez gonderilirdi. Bkz. PusulaRepository.GetPathologyFindingsByResultIdAsync.
public class PathologyFindingRecord
{
    public int ResultId { get; set; }

    // EPulse.IslemReferansNumarasi -- satir basina BENZERSIZ (canli veride dogrulandi:
    // 52.490 satirda 52.490 farkli deger, hic 0 yok). Observation'in ZORUNLU
    // local-system-unique-id extension'i icin dogal anahtar. Tekillestirmede grup basina
    // MIN() alinir -- yeni bloklar hep daha buyuk numara aldigi icin bu kimlik kararli kalir.
    public int IslemReferansNumarasi { get; set; }

    // SKRS ic kodu (orn. "1212" = "Meme, BBT") -- ICD-O-3'e cevrilmesi GEREKIR.
    public string? YerlesimYeriCode { get; set; }
    public string? YerlesimYeriValue { get; set; }

    // Zaten ICD-O-3 (orn. "8523/3") -- dogrudan kullanilir.
    public string? MorfolojiKoduCode { get; set; }
    public string? MorfolojiKoduValue { get; set; }

    // Observation.effectiveDateTime (1..1 ZORUNLU) icin. IstemZamani veritabaninda NOT NULL
    // ve canli veride hic bos degil -- bu yuzden effectiveDateTime her zaman doldurulabiliyor.
    // RaporlamaZamani ise 52.490 satirin 1.788'inde bos, o yuzden birincil kaynak DEGIL.
    public DateTime IstemZamani { get; set; }
    public DateTime? RaporlamaZamani { get; set; }
}
