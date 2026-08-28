namespace PusulaEHealthSync.Db;

// Genel Bakış panelindeki "İcbari Sigorta Gönderim Kapsamı" bölümü icin -- GetIslemlerByProtokolIdAsync
// ile AYNI eslesme kurallari (bkz. o metottaki tam gerekce), ama tek bir protokol yerine bir
// TARIH ARALIGINDAKI tum protokoller uzerinden. Fiyat/tutar alani KASITLI OLARAK yok -- Pazarlama.
// KurumHizmet'te gercek tarife/fiyat kolonu henuz dogrulanmadi (bkz. docs/bakanlik-sorulari.md
// benzeri, canli SELECT ile kullanicinin dogrulamasi gerekiyor), bu yuzden panelde kayip ciro
// TUTARI degil sadece ADET/oran gosteriliyor.
public class IcbariIslemRecord
{
    public int IslemId { get; set; }
    public int ProtokolId { get; set; }
    public string? PatientName { get; set; }
    public string? HizmetAdi { get; set; }
    public required string IcbariAdi { get; set; }
}
