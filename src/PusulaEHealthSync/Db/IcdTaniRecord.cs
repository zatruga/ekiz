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

    // AYNI HASTAYA AYNI ICD DAHA ONCE KONULDU MU? -> Condition.first-diagnosis
    //
    // BAKANLIK ISTEGI (2026-09-16 ve 2026-10-13, iki kez): "first-diagnosis
    // bilgisine rastlamadik, kaynak sisteminizde tutuluyorsa gonderime
    // eklenmesini rica ederiz."
    //
    // Pusula'da BOYLE BIR ALAN YOK. IlkTaniTarihi kolonu var ama 365 gunde
    // 186.038 kaydin yalnizca 99'unda dolu -- kullanilmiyor.
    //
    // ASIMETRI, ve isin anahtari bu: "false" DOGRULANABILIR BIR OLGU (ayni
    // hastada ayni ICD'yi gecmiste gorduysek, bu kesinlikle ilk degil), "true"
    // ise CIKARIM (gormediysek hasta baska hastanede tani almis olabilir, ya da
    // tani Pusula'ya gecmeden onceki doneme ait olabilir).
    //
    // Bu yuzden YALNIZCA false gonderiliyor; bulunamadiginda alan HIC
    // yazilmiyor (0..1). Olculdu (01.10 sonrasi 7.445 tani): 3.545'inde (%47,6)
    // gecmis kayit var.
    public bool DahaOnceKonuldu { get; set; }

    // Tedavi.ProtokolICD.MedulaTaniTipiId -- KAYNAK PUSULA'NIN KENDI KODU
    // (Tedavi.usp_GetEpikrizTani stored procedure'u, 2026-09-18):
    //   1 = On Tani   (Pre-Diagnosis)
    //   2 = Kesin Tani (Definitive Diagnosis)
    //   3 = Ayirici Tani (Differential Diagnosis)
    // Son 365 gunde 186.038 kaydin 184.711'inde (%99,3) dolu. nvarchar olarak
    // tutuluyor, o yuzden string okunuyor.
    public string? TaniTipiKodu { get; set; }
}
