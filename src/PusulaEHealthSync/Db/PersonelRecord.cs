namespace PusulaEHealthSync.Db;

// IK.Personel -> AZ Practitioner FHIR mapping icin kaynak. Kaynak: docs/practitioner-mapping.md.
// KARAR (2026-08-20, canli veriyle dogrulandi): hasta.protokol.DoktorId son 60 gunde
// SADECE PersonelTipiId=1 (Doktor, bkz. Pusula.PersonelTipi lookup) personeli referans
// ediyor -- bu yuzden Practitioner senkronu bu tipe sinirlandirildi.
public class PersonelRecord
{
    public const byte DoktorTipiId = 1;

    // ===== PRACTITIONER OLARAK GONDERILEBILEN PERSONEL TIPLERI (2026-10-13) =====
    //
    // KULLANICI (2026-10-13): "bu islemi yapan doktor olmayabilir, personelde olabilir
    // hemsirede."
    //
    // ESKI KISIT VE NEDEN ARTIK GECERLI DEGIL: yukaridaki 2026-08-20 karari Practitioner
    // senkronunu PersonelTipiId=1'e kilitlemisti, gerekce "hasta.protokol.DoktorId son 60
    // gunde sadece tip 1 referans ediyor" idi. O gerekce ENCOUNTER'IN DOKTORU icindi.
    // Procedure.performer eklenince referans verilen kume genisliyor, dolayisiyla kisit
    // da genislemek zorunda.
    //
    // TASKIN RISKI YOK: PractitionerSyncService id ile TALEP UZERINE calisiyor, toplu
    // degil -- yalnizca fiilen referans verdigimiz kisi gonderiliyor. Liste gene de ACIK
    // tutuluyor ki Pusula'da bir gun idari bir personel islem yapani olarak gorunurse
    // bakanliga "saglik calisani" diye gitmesin; o durumda kayit Skipped olur ve ekranda
    // sebebiyle GORUNUR.
    //
    // OLCULDU (01.10 sonrasi, 36.335 islem): fiilen islem yapan yalnizca iki tip var --
    // Doktor 35.653 (153 kisi) ve Fizyoterapist 16 (1 kisi, FIN'i uygun). Hemsirelerin
    // 313'u kayitli ve 291'inin FIN'i uygun ama hicbiri islem yapan olarak gorunmuyor;
    // yine de listeye alindi, cunku kullanicinin isaret ettigi durum yarin olusabilir.
    //
    // DISARIDA BIRAKILANLAR (klinik olmayan): Aktarim(0), Vezne(11), Medikal Muhasebe(12),
    // Cagri Merkezi(13), Idari Personel(14), Tibbi Sekreter(15), Satinalma(16),
    // Raportor(21), Kasa Teslim(22), Depocu(24), Gorusmeci(25), Arge(28), Mutfak(31),
    // Tercuman(33).
    public static readonly HashSet<byte> KlinikTipler =
    [
        1,  // Doktor
        2,  // Misafir Doktor
        3,  // Diş Doktoru
        4,  // Anestezist
        5,  // Laboratuar
        6,  // Radyoloji
        7,  // Hemşire
        8,  // Tıbbi Personel
        17, // Ebe
        18, // ATT
        19, // Sağlık Memuru
        20, // Patoloji Doktoru
        23, // Psikolog
        26, // İşyeri Hekimi
        27, // Tıbbi Laboratuar Uzmanı
        29, // Eczacı
        30, // Fizyoterapist
        32, // Diyetisyen
    ];

    public int Id { get; set; }
    public string? Adi { get; set; }
    public string? Soyadi { get; set; }
    public string? TCKimlikNo { get; set; }
    public DateTime? CikisTarihi { get; set; }
    public byte PersonelTipiId { get; set; }
}
