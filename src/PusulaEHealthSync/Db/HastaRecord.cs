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

    // az-newborn-patient icin -- kendi FIN'i henuz atanmamis yenidoganlarda anne FIN'i
    // uzerinden kimliklendirme yapilir, bkz. PatientMapper.
    public bool IsBizdeDogan { get; set; }
    public string? AnneTCKimlikNo { get; set; }
}
