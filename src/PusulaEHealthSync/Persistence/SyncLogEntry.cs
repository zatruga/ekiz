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
        "Observation" => "Tetkik",
        "DiagnosticReport" => "Radyoloji Raporu",
        "DiagnosticReport-Patoloji" => "Patoloji Raporu",
        _ => resourceType,
    };

    // SyncLog.ResourceType HER ZAMAN gercek bir FHIR resourceType degildir -- "DiagnosticReport-Patoloji"
    // sadece BIZIM ic takip etiketimiz (Radyoloji ile ayni FHIR kaynagini -- DiagnosticReport --
    // paylastigi icin PusulaId cakismasini onlemek amaciyla ayristirildi, bkz. PathologyReportMapper.
    // LocalUniqueId). e-Health API'sine GET/DELETE gibi gercek bir cagri yapilacaksa BURADAN
    // gecirilmeli -- aksi halde gecersiz bir resource type ile istek atilir.
    public static string FhirResourceType(string resourceType) => resourceType switch
    {
        "DiagnosticReport-Patoloji" => "DiagnosticReport",
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
        "Observation" => "rt-observation",
        "DiagnosticReport" => "rt-observation",
        "DiagnosticReport-Patoloji" => "rt-observation",
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
        if (m.Contains("Instance count for", StringComparison.OrdinalIgnoreCase) && m.Contains("cardinality", StringComparison.OrdinalIgnoreCase))
            return ("Zorunlu alan eksik/hatalı", "FHIR profilinde zorunlu tutulan bir alan boş bırakılmış ya da yanlış sayıda dolu");
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

    // KULLANICI ISTEGI (2026-08-29, Genel Bakış'ta canli veriyle test ederken): "hataları
    // daha anlaşılır gösteremez miyiz?" -- EHealthErrorFormatter'in cikardigi mesaj teknik
    // olarak dogru ama ham (orn. "HTTP 409: Non-existent reference: Practitioner/01a02302-
    // ...-6525140e95b9") -- ozellikle GUID'li referans hatalari hastane IT personeli icin
    // "ne yapmam lazim" sorusuna cevap vermiyor.
    //
    // GENISLETME (2026-08-29, kullanici tekrar sikayet etti -- "bu hata mesajlarını sana bir
    // çok kez dedim anlaşılır bir sekilde yorumlayarak göster"): mesaj sunucudan genelde " | "
    // ile ayrilmis BIRDEN FAZLA sorunu tek satirda listeler (orn. "Instance count for
    // 'Observation.value[x].unit' is 0 ... | Instance count for '...system' is 0 ..."). Eskiden
    // sadece TEK bir bilinen kalibi (Non-existent reference) taniyip gerisini oldugu gibi
    // basiyordu -- bu yuzden "Instance count ... cardinality" (FHIR zorunlu alan eksik) gibi
    // COK SIK cikan bir kalip hala ham gorunuyordu. Artik her " | " parcasi AYRI AYRI
    // yorumlanip birlestiriliyor -- taniyamadigi bir parca icin o parcayi oldugu gibi
    // dondurur, asla "bilinmeyen hata" gibi bilgi kaybettiren bir metinle degistirmez.
    public static string FriendlyError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Sebep belirtilmedi";

        var segments = message.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return message;

        var friendly = segments.Select(InterpretSegment).Distinct().ToList();
        return string.Join(" ", friendly);
    }

    private static string InterpretSegment(string segment)
    {
        var refMatch = System.Text.RegularExpressions.Regex.Match(segment, @"[Nn]on-existent reference:\s*(\w+)/");
        if (refMatch.Success)
        {
            var refType = ResourceTypeLabel(refMatch.Groups[1].Value);
            return $"Bağlı olduğu {refType} kaydı e-Health'te artık bulunamıyor (silinmiş ya da hiç gönderilmemiş olabilir) -- önce {refType} tekrar gönderilmeli.";
        }

        var cardMatch = System.Text.RegularExpressions.Regex.Match(
            segment, @"Instance count for '([^']+)' is (\d+), which is not within the specified cardinality of (\d+)\.\.(\*|\d+)");
        if (cardMatch.Success)
        {
            var fieldPath = cardMatch.Groups[1].Value;
            var actual = int.Parse(cardMatch.Groups[2].Value);
            var min = int.Parse(cardMatch.Groups[3].Value);
            var fieldLabel = FriendlyFieldName(fieldPath);
            return actual < min
                ? $"Zorunlu bir alan eksik: {fieldLabel}."
                : $"'{fieldLabel}' alanında beklenenden fazla değer gönderilmiş.";
        }

        var (label, _) = ErrorCategory(segment);
        return label switch
        {
            "FIN formatı hatalı" => "TC Kimlik/FIN numarası AZ FIN biçimine uymuyor -- Pusula'daki hasta kaydı kontrol edilmeli.",
            "ICD tanı eksik/geçersiz" => "Protokolde geçerli bir ICD-10 tanı kodu yok -- Pusula'da tanı girilmeli.",
            "Zaman aşımı / bağlantı" => "e-Health sunucusu zamanında yanıt vermedi -- bağlantı sorunu olabilir, tekrar denenmeli.",
            "e-Health bağlantı ayarı eksik" => "Ayarlar sayfasında e-Health bağlantı bilgileri eksik ya da hatalı.",
            "Referans bulunamadı" => "Bağlı bir kayıt e-Health'te artık mevcut değil -- önce o kayıt tekrar gönderilmeli.",
            _ => segment.Trim(),
        };
    }

    // FHIR alan yolunu ("Observation.value[x].unit" gibi) hastane IT personelinin anlayacagi
    // bir Turkce etikete cevirir -- teknik yolu da parantez icinde SAKLAR (bilgi kaybetmemek
    // icin), sadece taniyamadigi bir alan icin ham yolu oldugu gibi doner.
    private static string FriendlyFieldName(string fieldPath)
    {
        var lastSegment = fieldPath.Split('.')[^1].Split(':')[^1];
        var label = lastSegment switch
        {
            "unit" => "sonuç birimi",
            "system" => "kod sistemi",
            "code" => "kod",
            "display" => "görünen ad",
            "value[x]" => "sonuç değeri",
            "subject" => "hasta referansı",
            "encounter" => "müayinə referansı",
            "procedure-code" => "prosedür/İcbari kodu",
            "local-system-unique-id" => "sistem içi kimlik",
            "identifier" => "kimlik numarası",
            "status" => "durum",
            "category" => "kategori",
            "extension" => "ek alan (extension)",
            _ => null,
        };
        return label is null ? fieldPath : $"{label} ({fieldPath})";
    }
}
