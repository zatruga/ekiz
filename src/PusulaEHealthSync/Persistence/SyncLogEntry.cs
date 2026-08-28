namespace PusulaEHealthSync.Persistence;

public enum SyncStatus { Success, Skipped, Failed }
public enum SyncOperation { Validate, Create, Update, Delete }

// Her senkron denemesinin sonucu -- dashboard'un dogrudan uzerine kurulacagi tablo.
// Amac: "hangi kayit gonderildi, hangisi atlandi, hangisi hata aldi, neden" sorusunu
// koda bakmadan cevaplayabilmek.
public class SyncLogEntry
{
    public long Id { get; set; }
    public required string ResourceType { get; set; }       // "Patient", "Encounter" ...
    public required int PusulaId { get; set; }               // hasta.hasta.Id / hasta.protokol.Id
    public required SyncStatus Status { get; set; }
    public SyncOperation? Operation { get; set; }             // Skipped ise null
    public string? AzResourceId { get; set; }                 // basarili Create/Update'te donen id
    public string? Message { get; set; }                      // atlanma nedeni veya hata mesaji

    // Dashboard tablosunda dogrudan gosterebilmek icin -- mapping denemesi hangi
    // sonucla bitmis olursa olsun (basarili/atlanan/hatali), elimizdeki Pusula
    // kaynak verisinden aliniyor. RequestJson'i parse etmeye gerek kalmasin diye.
    public string? PatientFullName { get; set; }
    public string? FathersName { get; set; }
    public DateOnly? BirthDate { get; set; }
    public string? Gender { get; set; }
    public string? Fin { get; set; }                          // hasta.hasta.TCKimlikNo (FIN olarak gonderilen deger)
    public DateTime? RecordOpenedAt { get; set; }              // hasta.hasta.CreatedDate -- Pusula'da kayit acilma tarihi
    public string? RequestJson { get; set; }                  // gonderilen payload (debug/dashboard icin)
    public string? ResponseJson { get; set; }                 // sunucudan donen ham yanit
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // KARAR/DUZELTME (2026-08-20): "Basarili" durumu Validate (sadece kontrol, hicbir sey
    // kalici olarak KAYDEDILMEZ) ile Create/Update (gercekten e-Health'e YAZILIR) arasinda
    // ayrim yapmiyordu -- dashboard'da ikisi de yesil "Gönderildi" rozeti olarak gorunuyordu.
    // Bu, kullaniciyi bir kaydin e-Health'te GERCEKTEN var oldugunu sanmaya yoneltiyordu
    // (orn. Patient sadece dogrulanmisken Encounter "hasta bulunamadi" diye atlaniyor, kafa
    // karistiriyordu). Rozet metni artik Operation'a gore ayrisiyor.
    public static string SuccessLabel(SyncOperation? operation) => operation switch
    {
        SyncOperation.Validate => "Doğrulandı",
        SyncOperation.Delete => "Silindi",
        _ => "Gönderildi",
    };

    // Protokol.cshtml'deki checklist basliklarinda (Hasta/Doktor/Müayinə/Epikriz) kullanilan
    // ayni Turkce/Azerice etiket -- Kayit Detayi sayfasinda da AYNI isim kullanilsin diye
    // (KULLANICI ISTEGI, 2026-08-21: "hangi kısmın detayında olduğumu göremiyorum" -- sadece
    // "Composition" gibi FHIR terimi tek basina yeterince acik degildi).
    public static string ResourceTypeLabel(string resourceType) => resourceType switch
    {
        "Patient" => "Hasta",
        "Practitioner" => "Doktor",
        "Encounter" => "Müayinə",
        "Composition" => "Epikriz",
        "Condition" => "Tanı",
        "Procedure" => "İşlem",
        _ => resourceType,
    };

    // Aktivite Akisi tablosundaki "Tur" rozeti ve Kayit Detayi'ndaki resource-chip
    // AYNI renk koduna sahip olsun diye (rt-patient/rt-practitioner/...) tek yerden --
    // KULLANICI ISTEGI (2026-08-25): "doktor gondermisim ama hasta gibi gorunuyor,
    // ne gonderildigini bilmiyorum".
    public static string ResourceTypeCssClass(string resourceType) => resourceType switch
    {
        "Patient" => "rt-patient",
        "Practitioner" => "rt-practitioner",
        "Encounter" => "rt-encounter",
        "Composition" => "rt-composition",
        "Condition" => "rt-condition",
        "Procedure" => "rt-procedure",
        _ => "rt-patient",
    };

    // Dashboard'daki 6 farkli yerde (Protokol Listesi x2, Protokol Detay x2, Kayit
    // Detayi, Aktivite Akisi) neredeyse ayni durum-rozeti mantigi tekrarlanmisin diye tek
    // yerden -- CSS sinifi + metin. Silme sonrasi en son kayit Status=Success,
    // Operation=Delete olur; bunu yesil "basarili" degil noturn "Silindi" olarak gostermek
    // ONEMLI -- aksi halde silinmis bir kaydin hala e-Health'te varmis gibi gorunmesi riski var.
    // DUZELTME (2026-08-20, canli olayda bulundu): basarisiz bir SILME denemesi de
    // (orn. hala baska kayitlarca referans edildigi icin HTTP 409 ile reddedilen) diger
    // her turlu hata ile AYNI kirmizi "Hatalı" rozetini gosteriyordu -- kullaniciya
    // "gonderim basarisiz/kayit e-Health'te yok" izlenimi veriyordu, oysa TAM TERSI: kayit
    // hala e-Health'te GUVENDE, sadece silinemedi. Bu iki durum kokten farkli anlamlar
    // tasiyor, ayni rozetle gosterilmemeli.
    public static (string CssClass, string Label) StatusBadge(SyncLogEntry? entry)
    {
        if (entry is null) return ("neutral", "Gönderilmedi");
        return entry switch
        {
            { Status: SyncStatus.Success, Operation: SyncOperation.Delete } => ("neutral", "Silindi"),
            { Status: SyncStatus.Failed, Operation: SyncOperation.Delete, AzResourceId: not null } => ("success", "Gönderildi (silinemedi)"),
            { Status: SyncStatus.Failed, Operation: SyncOperation.Delete } => ("warning", "Silme hatalı"),
            { Status: SyncStatus.Success } => ("success", SuccessLabel(entry.Operation)),
            { Status: SyncStatus.Failed } => ("danger", "Hatalı"),
            { Status: SyncStatus.Skipped } => ("warning", "Atlandı"),
            _ => ("neutral", "Gönderilmedi"),
        };
    }

    // "Sil" butonunu gostermek icin -- DUZELTME (2026-08-20): eskiden "Operation != Delete"
    // yeterli sanilmisti, ama BASARISIZ bir silme denemesinden sonra da Operation=Delete
    // oluyor (kayit hala e-Health'te durmasina ragmen) -- bu da butonun yanlislikla
    // kaybolmasina yol aciyordu. Sadece GERCEKTEN silinmis (Success+Delete) kayitlarda
    // buton gizlenmeli.
    public static bool CanDelete(SyncLogEntry? entry) =>
        entry is { AzResourceId: not null } && entry is not { Status: SyncStatus.Success, Operation: SyncOperation.Delete };

    // Genel Bakış panelindeki "Hata Kategorileri" icin -- EHealthErrorFormatter zaten her
    // hatada okunabilir, detayli bir mesaj uretiyor (bkz. o dosya), ama tek tek yuzlerce
    // satiri okumak yerine "hangi TUR hata ne kadar sik" sorusuna cevap lazim. Burada anahtar
    // kelime eslestirmesiyle mesaj TEKRAR (SQL'de degil, sadece sunum katmaninda) gruplaniyor
    // -- EHealthErrorFormatter'in kendi cikardigi metni degistirmiyor, sadece siniflandiriyor.
    public static (string Label, string Description) ErrorCategory(string? message)
    {
        var m = message ?? "";
        if (m.Contains("FIN", StringComparison.OrdinalIgnoreCase))
            return ("FIN formatı hatalı", "TC Kimlik/FIN alanı AZ FIN biçimine uymuyor");
        if (m.Contains("ICD", StringComparison.OrdinalIgnoreCase) || m.Contains("tanı", StringComparison.OrdinalIgnoreCase))
            return ("ICD tanı eksik/geçersiz", "Protokolde tanı yok ya da AZ CodeSystem'de karşılığı bulunamadı");
        if (m.Contains("zaman aşımı", StringComparison.OrdinalIgnoreCase) || m.Contains("timeout", StringComparison.OrdinalIgnoreCase) || m.Contains("yanıt ver", StringComparison.OrdinalIgnoreCase))
            return ("Zaman aşımı / bağlantı", "e-Health sunucusu süresi içinde yanıt vermedi");
        if (m.Contains("e-Health", StringComparison.OrdinalIgnoreCase) && (m.Contains("adres", StringComparison.OrdinalIgnoreCase) || m.Contains("BaseUrl", StringComparison.OrdinalIgnoreCase) || m.Contains("kimlik", StringComparison.OrdinalIgnoreCase)))
            return ("e-Health bağlantı ayarı eksik", "Ayarlar sayfasında Test/Canlı ortam bilgisi eksik ya da hatalı");
        if (m.Contains("409") || m.Contains("bulunamadı", StringComparison.OrdinalIgnoreCase) || m.Contains("referans", StringComparison.OrdinalIgnoreCase) || m.Contains("reference", StringComparison.OrdinalIgnoreCase))
            return ("Referans bulunamadı", "Bağlı bir kayıt (Hasta/Müayinə) e-Health'te artık mevcut değil");
        return ("Diğer", "Yukarıdaki kategorilere girmeyen tekil hatalar");
    }
}
