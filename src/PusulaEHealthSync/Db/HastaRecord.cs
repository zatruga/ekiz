namespace PusulaEHealthSync.Db;

public class HastaRecord
{
    public int Id { get; set; }
    public string? Adi { get; set; }
    public string? Adi2 { get; set; }
    public string? Soyadi { get; set; }
    public string? BabaAdi { get; set; }
    public DateTime? DogumTarihi { get; set; }
    public string? CinsiyetId { get; set; }
    public bool? AktifHastaId { get; set; }
    public int? KanGrubuId { get; set; }
    public int? MedeniHaliId { get; set; }
    public string? GSM { get; set; }
    public string? SabitTel { get; set; }
    public string? Email { get; set; }
    public string? TCKimlikNo { get; set; }
    public DateTime? CreatedDate { get; set; }

    // hasta.hasta.ModifiedDate -- "degismediyse yeniden gonderme" kontrolu icin
    // (2026-10-13). Olculdu: 01.10 sonrasi 1.794 hastanin 776'sinda dolu, kalaninda
    // NULL (hic degistirilmemis kayitlar). Bu yuzden okuyan taraf CreatedDate'e
    // dusuyor -- vital bulgulardaki ayni desen (VitalSignsSyncService).
    public DateTime? ModifiedDate { get; set; }

    // hasta.hasta.EvAdresi -> Patient.address (bakanlik istegi 2026-10-13,
    // kullanici karari 2026-10-14: "Evadresi sutununu kullanacagiz").
    // SERBEST METIN -- icerigi adresten sehir adina, karalamaya kadar degisiyor;
    // ayiklama kurallari AzAdres'te, olcumleriyle birlikte.
    public string? EvAdresi { get; set; }

    // Ortak.Il / Ortak.Ilce'den COZULMUS adlar (2026-10-14).
    //
    // KULLANICI: "IlIdEv ve IlceIdEv alanlarida var." Haklydi -- ilk taramada bu
    // tablolar bulunamamisti cunku LOGIN ONLARI sys.objects'te GOREMIYOR; ama
    // SELECT edilebiliyorlar. (Ayni tuzak projede sys.indexes icin de yazili:
    // katalog gorunurlugu yetkiye bagli, "0 satir" = "yok" demek degil.)
    // Pusula'nin kendi Hasta.uv_Hasta view'i de tam bu iki alt sorguyu kullaniyor.
    //
    // Olculdu (01.10 sonrasi): IlIdEv 6.030/6.136 dolu (%98,3), 27 ayri deger --
    // 6.089'u "Bakü". IlceIdEv ise fiilen hep 3126 = "BAKÜ", yani ilin tekrari.
    public string? IlAdi { get; set; }
    public string? IlceAdi { get; set; }

    // az-newborn-patient icin -- kendi FIN'i henuz atanmamis yenidoganlarda anne FIN'i
    // uzerinden kimliklendirme yapilir, bkz. PatientMapper.
    public bool IsBizdeDogan { get; set; }
    public string? AnneTCKimlikNo { get; set; }
}
