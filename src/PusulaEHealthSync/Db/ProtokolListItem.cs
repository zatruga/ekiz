namespace PusulaEHealthSync.Db;

// hasta.protokol'de ayri bir "protokol no" alani yok -- Id, Patient/Practitioner'daki
// gibi local-system-unique-id olarak da kullanilan operasyonel numaradir (bkz.
// docs/encounter-mapping.md bolum 1).
public class ProtokolListItem
{
    public int ProtokolId { get; set; }
    public int HastaId { get; set; }
    public string? HastaAdi { get; set; }
    public string? HastaSoyadi { get; set; }
    public string? Fin { get; set; }
    public int? DoktorId { get; set; }
    public string? DoktorAdi { get; set; }
    public string? DoktorSoyadi { get; set; }
    public int? BolumId { get; set; }
    public string? BolumAdi { get; set; }
    public string? GelisTipiId { get; set; }   // A / Y / G

    // Pusula.ProtokolTipi lookup tablosu DB'de BOS (adlar uygulama tarafinda hardcoded,
    // 2026-08-21'de dogrulandi) -- bu yuzden burada sadece ham Id tutuluyor, isim
    // eslemesi yok. KULLANICI ISTEGI (2026-08-21): "reçete tipi protokolleri gönderme"
    // -- hangi Id'nin Reçete'ye karsilik geldigi kullanicidan onay bekliyor.
    public byte? ProtokolTipiId { get; set; }
    public DateTime? AcilisTarihi { get; set; }
    public DateTime? KapanisTarihi { get; set; }

    // hasta.protokol.State -- 0: iptal/silinmis, 1: acik, 2: kapali (canli veriden
    // dogrulandi, 2026-08-20). State=0 protokoller Pusula'nin kendi raporlarinda da
    // sayilmiyor -- gonderim listemizde de gosterilmemeli/gonderilmemeli.
    public byte State { get; set; }
    public bool IsVoided => State == 0;

    public string HastaAdiSoyadi => string.Join(" ", new[] { HastaAdi, HastaSoyadi }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string? DoktorAdiSoyadi => string.IsNullOrWhiteSpace(DoktorAdi) && string.IsNullOrWhiteSpace(DoktorSoyadi)
        ? null
        : string.Join(" ", new[] { DoktorAdi, DoktorSoyadi }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
