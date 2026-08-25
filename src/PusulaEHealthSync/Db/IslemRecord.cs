namespace PusulaEHealthSync.Db;

// Hasta.ProtokolIslem (Ortak.Hizmet ile JOIN) -- bir protokole uygulanmis hizmet/islem.
// KAYNAK BULUNDU (2026-08-25, kullanici istegi -- AZ Procedure): Hasta.ProtokolIslem
// (9M+ satir, gercek/aktif kullanilan tablo) hem laboratuvar testlerini hem gercek
// klinik prosedurleri hem de idari/faturalama kalemlerini (orn. ameliyathane acilis
// ucreti) AYNI tabloda karisik tutuyor -- bunlarin hicbirini ayirt eden temiz bir "tur"
// alani yok. KULLANICI KARARI (2026-08-25): ayrim icin filtre "sadece ICBARI SIGORTA
// FIYAT LISTESI (KurumHizmetKategoriId=13) ile eslestirilmis olanlari gonderecegiz" --
// bu dogal olarak saf idari/dahili faturalama kalemlerini disarida birakiyor (onlarin
// icbari eslestirmesi olmuyor). IcbariKodu/IcbariAdi CANLI sorgulaniyor (Excel DEGIL --
// kullanici: "eslesmeler degisebilir, anlik takip etmek gerekir").
public class IslemRecord
{
    public int Id { get; set; }
    public int ProtokolId { get; set; }
    public int HizmetId { get; set; }
    public string? HizmetAdi { get; set; }
    public DateTime? IslemTarihi { get; set; }
    public required string IcbariKodu { get; set; }
    public required string IcbariAdi { get; set; }
}
