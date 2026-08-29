namespace PusulaEHealthSync.Db;

// LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId -- Pusula'nin kendi eski LIS.* tablolari
// TERK EDILMIS (bos), gercek sonuclar ayri bir baglı sunucudaki (COMED LIS) tablolardan bu
// view uzerinden okunuyor (bkz. konusma, 2026-08-21 -- view tanimi COMED.LIS_BAK.LIS.TestProcess
// vb. tablolara JOIN yapiyor). Her satir TEK BIR test parametresinin sonucu (panel testlerde
// bir protokolde onlarca satir olabilir, orn. 61).
//
// Status/State: view'in kendi CASE'i LTP.Status'u normalize ediyor -- CANLI VERIYLE
// DOGRULANDI (2026-08-21, son 3 gun/79 protokol): Status=6 olan satirlarin TAMAMINDA
// (1659/1659) TetkikSonucOnayTarihi DOLU -- yani "sonuc onaylandi/kesinlesti" sinyali. Digger
// Status degerleri (2/3/8) hicbirinde onay tarihi yok (hala islemde). KilitDurumuId=1
// (Epikriz) ile AYNI kalip: SADECE onaylanmis (Status=6) sonuclar gonderilmeli.
public class LabResultRecord
{
    public int LabaratuarSonucId { get; set; }
    public int VisitId { get; set; } // ProtokolId
    public int Status { get; set; }
    public string? TetkikAdi { get; set; }
    public string? TetkikSonucu { get; set; }
    public string? TetkikSonucuBirimi { get; set; }
    public string? TetkikSonucuReferansDegeri { get; set; }

    // GUVENILMEZ (2026-08-29, canli hata -- kullanici: "test sonucu referans değerin içinde
    // olmasına rağmen neden referans dışı vermiş"): bu alanin "1" degerinin gercekte ne
    // anlama geldigi 2026-08-21'de "TERSTIR" diye not edilmisti, ama canli veride (orn. 43.1,
    // 38.00-52.0 araliginda oldugu HALDE DisindaMi=true) bu yorum da dogrulanmiyor. Anlami
    // KESIN olarak cozulene kadar hicbir yerde (UI'da "Referans dışı" rozeti, FHIR
    // Observation.interpretation) KULLANILMIYOR -- yanlis "anormal" isareti hem hastaneye hem
    // e-Health'e gitmesin diye. Ham deger yine de okunuyor, ileride cozulunce buradan acilir.
    public bool DisindaMi { get; set; }
    public string? LoincKodu { get; set; }
    public DateTime? TetkikSonucTarihi { get; set; }
    public DateTime? TetkikSonucOnayTarihi { get; set; }

    // AZ Observation icin ZORUNLU procedure-code extension'ini doldurabilmek icin --
    // LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId (COMED bagli sunucu) kendisi bir
    // Hizmet/ProtokolIslem baglantisi vermiyor, ama LoincKodu'yu LIS.Test.LoincKodu ile
    // eslestirip oradan HizmetId -> Icbari kodu zincirine ulasilabildigi CANLI dogrulandi
    // (2026-08-29, kullanici SELECT'i). Bkz. PusulaRepository.GetLabResultsByProtokolIdAsync.
    // Alt parametrenin (EOS %, RDW-SD vb.) KENDI Icbari eslesmesi yoksa (genelde panel bir
    // butun olarak faturalandigi icin ayri bir kaydi olmuyor), panelin (ust testin, orn.
    // "Hemogram") Icbari koduna otomatik dusuyor (KULLANICI KARARI, 2026-08-29).
    public string? IcbariKodu { get; set; }
    public string? IcbariAdi { get; set; }

    // Protokol Detay'da alt parametreleri (RDW-SD, EOS % vb.) ana testin (Hemogram vb.)
    // altinda gruplamak icin -- LIS.TestParametre (TestId=ust panel, AltTestId=alt parametre)
    // uzerinden bulunan ust test adi. Bu satirin KENDISI bir panelin alt parametresi degilse
    // (bagimsiz bir test, ya da panelin kendisi -- orn. "Hemogram" satirinin kendisi) null.
    public string? PanelAdi { get; set; }

    public bool IsApproved => Status == 6;
}
