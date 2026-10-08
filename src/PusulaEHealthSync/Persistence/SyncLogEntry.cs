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

    // Bu deneme HANGI ORTAMA gitti: "Test" (sandbox) ya da "Live". 2026-10-05'te eklendi.
    // Sandbox'a gonderilmis bir kaydin canli ortamda "gonderilmis" sayilmasi, canliya
    // gecildiginde hicbir seyin gonderilmemesi demekti (bkz. SyncLogStore basindaki not).
    // null = eski kayit; okunurken "Test" varsayiliyor.
    public string? Ortam { get; set; }

    // KARAR/DUZELTME (2026-08-20): "Basarili" durumu Validate (sadece kontrol, hicbir sey
    // kalici olarak KAYDEDILMEZ) ile Create/Update (gercekten TRƏS'e YAZILIR) arasinda
    // ayrim yapmiyordu -- dashboard'da ikisi de yesil "Gönderildi" rozeti olarak gorunuyordu.
    // Bu, kullaniciyi bir kaydin TRƏS'te GERCEKTEN var oldugunu sanmaya yoneltiyordu
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
        "Composition-Patoloji" => "Patoloji Belgesi",
        "Observation-Patoloji" => "Patoloji Bulgusu",
        _ => resourceType,
    };

    // SyncLog.ResourceType HER ZAMAN gercek bir FHIR resourceType degildir -- "DiagnosticReport-Patoloji"
    // sadece BIZIM ic takip etiketimiz (Radyoloji ile ayni FHIR kaynagini -- DiagnosticReport --
    // paylastigi icin PusulaId cakismasini onlemek amaciyla ayristirildi, bkz. PathologyReportMapper.
    // LocalUniqueId). TRƏS API'sine GET/DELETE gibi gercek bir cagri yapilacaksa BURADAN
    // gecirilmeli -- aksi halde gecersiz bir resource type ile istek atilir.
    public static string FhirResourceType(string resourceType) => resourceType switch
    {
        "DiagnosticReport-Patoloji" => "DiagnosticReport",
        "Composition-Patoloji" => "Composition",
        "Observation-Patoloji" => "Observation",
        // Vital bulgular da ayni kalibi kullaniyor: FHIR'de Observation, SyncLog'da ayri bir
        // etiket (laboratuvarla PusulaId cakismasin diye -- vitalde PusulaId GenelMuayene.Id,
        // labda LabaratuarSonucId). 2026-09-30'a kadar bu satir EKSIKTI: bir vital kaydi
        // silinmeye kalkilsaydi istek /fhir/Observation-Vital/... adresine giderdi.
        "Observation-Vital" => "Observation",
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
        "Composition-Patoloji" => "rt-composition",
        "Observation-Patoloji" => "rt-observation",
        _ => "rt-patient",
    };

    // Dashboard'daki 6 farkli yerde (Protokol Listesi x2, Protokol Detay x2, Kayit
    // Detayi, Aktivite Akisi) neredeyse ayni durum-rozeti mantigi tekrarlanmisin diye tek
    // yerden -- CSS sinifi + metin. Silme sonrasi en son kayit Status=Success,
    // Operation=Delete olur; bunu yesil "basarili" degil noturn "Silindi" olarak gostermek
    // ONEMLI -- aksi halde silinmis bir kaydin hala TRƏS'te varmis gibi gorunmesi riski var.
    // DUZELTME (2026-08-20, canli olayda bulundu): basarisiz bir SILME denemesi de
    // (orn. hala baska kayitlarca referans edildigi icin HTTP 409 ile reddedilen) diger
    // her turlu hata ile AYNI kirmizi "Hatalı" rozetini gosteriyordu -- kullaniciya
    // "gonderim basarisiz/kayit TRƏS'te yok" izlenimi veriyordu, oysa TAM TERSI: kayit
    // hala TRƏS'te GUVENDE, sadece silinemedi. Bu iki durum kokten farkli anlamlar
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
    // oluyor (kayit hala TRƏS'te durmasina ragmen) -- bu da butonun yanlislikla
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

        // ESLESTIRME EKSIGI -- "Referans bulunamadi"DAN ONCE BAKILIYOR, SIRA ONEMLI.
        //
        // KULLANICI (2026-10-08): bir idrar tetkiki icin ekranda "Bağlı bir kayıt TRƏS'te
        // artık mevcut değil -- önce o kayıt tekrar gönderilmeli" yaziyordu ve hakli olarak
        // "hangi kaydi tekrar gondereyim" diye sordu. Gonderilecek bir kayit YOKTU.
        //
        // Sebep asagidaki referans dalinin "bulunamadı" kelimesini yakalamasiydi: ham mesaj
        // "İcbari Sigorta Fiyat Listesi eşleşmesi BULUNAMADI" diyor, ama buradaki
        // "bulunamadi" TRƏS'teki bir kaydi degil, PUSULA'daki eslestirme tablosunda bir
        // satirin olmadigini anlatiyor. Ikisi tamamen farkli isler gerektiriyor:
        // biri yeniden gonderim, digeri veri tanimi.
        //
        // Olculdu (sunucu gunlugu, 2026-10-08): atlanan laboratuvar kayitlarinin %70'i
        // (21.076 kayit) bu kaliptaydi ve hepsi yanlis kategoride gorunuyordu.
        if (m.Contains("İcbari", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Icbari", StringComparison.OrdinalIgnoreCase)
            || m.Contains("LOINC", StringComparison.OrdinalIgnoreCase)
            || m.Contains("procedure-code", StringComparison.OrdinalIgnoreCase))
            return ("Eşleştirme eksik", "Pusula'daki tanım eksik: hizmetin İcbari karşılığı ya da tetkikin LOINC kodu yok");

        if (m.Contains("Instance count for", StringComparison.OrdinalIgnoreCase) && m.Contains("cardinality", StringComparison.OrdinalIgnoreCase))
            return ("Zorunlu alan eksik/hatalı", "FHIR profilinde zorunlu tutulan bir alan boş bırakılmış ya da yanlış sayıda dolu");
        if (m.Contains("FIN", StringComparison.OrdinalIgnoreCase))
            return ("FIN formatı hatalı", "TC Kimlik/FIN alanı AZ FIN biçimine uymuyor");
        if (m.Contains("ICD", StringComparison.OrdinalIgnoreCase) || m.Contains("tanı", StringComparison.OrdinalIgnoreCase))
            return ("ICD tanı eksik/geçersiz", "Protokolde tanı yok ya da AZ CodeSystem'de karşılığı bulunamadı");
        if (m.Contains("zaman aşımı", StringComparison.OrdinalIgnoreCase) || m.Contains("timeout", StringComparison.OrdinalIgnoreCase) || m.Contains("yanıt ver", StringComparison.OrdinalIgnoreCase))
            return ("Zaman aşımı / bağlantı", "TRƏS sunucusu süresi içinde yanıt vermedi");
        if (m.Contains("TRƏS", StringComparison.OrdinalIgnoreCase) && (m.Contains("adres", StringComparison.OrdinalIgnoreCase) || m.Contains("BaseUrl", StringComparison.OrdinalIgnoreCase) || m.Contains("kimlik", StringComparison.OrdinalIgnoreCase)))
            return ("TRƏS bağlantı ayarı eksik", "Ayarlar sayfasında Test/Canlı ortam bilgisi eksik ya da hatalı");
        if (m.Contains("409") || m.Contains("bulunamadı", StringComparison.OrdinalIgnoreCase) || m.Contains("referans", StringComparison.OrdinalIgnoreCase) || m.Contains("reference", StringComparison.OrdinalIgnoreCase))
            return ("Referans bulunamadı", "Bağlı bir kayıt (Hasta/Müayinə) TRƏS'te artık mevcut değil");
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

    // KULLANICI ISTEGI (2026-10-02): "hatanın tercümesi olmalı, Türkçe, anlaşılır bir dil
    // olmalı; hiç bilmeyen bir kişi bunun ne olduğunu, hatanın neden kaynaklandığını
    // anlamalı."
    //
    // Her cevirinin uc isi var: NE oldu, NEDEN oldu, NE YAPILMALI. Teknik terim (cardinality,
    // constraint, reference) kullaniciya hicbir sey anlatmiyor -- karsiliklari yazildi.
    //
    // KALIPLAR TAHMIN DEGIL, OLCUM: yerel senkron gunlugundeki BASARISIZ kayitlarin tamami
    // (21 farkli mesaj) gruplanip en sik gorulenden baslanarak karsilandi. Taninmayan bir
    // mesaj gelirse ham metin yine gosteriliyor -- sessizce yutulmuyor.
    private static string InterpretSegment(string segment)
    {
        var s = segment.Trim();

        // -- Is kurali ihlalleri (sunucu "Business rules validation failed: <kural>" doner) --
        var kural = System.Text.RegularExpressions.Regex.Match(
            s, @"Business rules validation failed:\s*(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (kural.Success)
        {
            var metin = kural.Groups[1].Value.Trim();

            if (metin.Contains("Procedure date must not be before Encounter start", StringComparison.OrdinalIgnoreCase))
                return "İşlemin yapıldığı tarih, müayinənin başlangıç tarihinden ÖNCE görünüyor. "
                     + "TRƏS, protokol açılmadan önce yapılmış bir işlemi kabul etmiyor. "
                     + "Pusula'da ya işlem tarihi ya da protokol açılış saati yanlış girilmiş olabilir -- "
                     + "ikisi karşılaştırılıp düzeltilmeli.";

            if (metin.Contains("must not be before", StringComparison.OrdinalIgnoreCase)
                || metin.Contains("must not be after", StringComparison.OrdinalIgnoreCase))
                return $"Tarih sırası TRƏS'in kabul ettiği aralığın dışında: {metin} "
                     + "Pusula'daki tarihler kontrol edilmeli.";

            return $"TRƏS'in bir iş kuralı bu kaydı reddetti: {metin}";
        }

        // -- Baglantili kayit TRƏS'te yok --
        var refMatch = System.Text.RegularExpressions.Regex.Match(s, @"[Nn]on-existent reference:\s*(\w+)/");
        if (refMatch.Success)
        {
            var refType = ResourceTypeLabel(refMatch.Groups[1].Value);
            return $"Bağlı olduğu {refType} kaydı TRƏS'te bulunamıyor (silinmiş ya da hiç gönderilmemiş olabilir). "
                 + $"Bu kayıt tek başına gönderilemez -- önce {refType} gönderilmeli.";
        }

        // -- Laboratuvar: ne degeri ne bileseni var --
        if (s.Contains("az-lab-value-or-component", StringComparison.OrdinalIgnoreCase))
            return "Bu laboratuvar kaydının ne kendi sonuç değeri var ne de alt parametresi. "
                 + "TRƏS boş bir tetkik kaydını kabul etmiyor. "
                 + "Genellikle panelin sipariş/toplayıcı satırıdır; sonuç girilince gönderilebilir hale gelir.";

        // -- Silinemiyor: baska kayitlar referans veriyor --
        if (s.Contains("There are other resources referencing this resource", StringComparison.OrdinalIgnoreCase))
            return "Bu kayıt silinemiyor çünkü TRƏS'te ona bağlı başka kayıtlar duruyor. "
                 + "Silme işlemi dıştan içe doğru yapılmalı -- önce ona bağlı kayıtlar, sonra bu kayıt.";

        // -- FIN bicimi --
        if (s.Contains("az-practitioner-fin-format", StringComparison.OrdinalIgnoreCase))
            return "Doktorun FIN numarası TRƏS'in beklediği biçimde değil (7 harf/rakam olmalı). "
                 + "Pusula'daki doktor kaydındaki kimlik numarası düzeltilmeli.";
        if (s.Contains("az-fin-format", StringComparison.OrdinalIgnoreCase))
            return "Hastanın FIN numarası TRƏS'in beklediği biçimde değil (7 harf/rakam olmalı). "
                 + "Pusula'daki hasta kaydındaki kimlik numarası düzeltilmeli.";

        // -- Zorunlu alan / sayi uyusmazligi --
        var cardMatch = System.Text.RegularExpressions.Regex.Match(
            s, @"Instance count for '([^']+)' is (\d+), which is not within the specified cardinality of (\d+)\.\.(\*|\d+)");
        if (cardMatch.Success)
        {
            var fieldPath = cardMatch.Groups[1].Value;
            var actual = int.Parse(cardMatch.Groups[2].Value);
            var min = int.Parse(cardMatch.Groups[3].Value);
            var fieldLabel = FriendlyFieldName(fieldPath);
            return actual < min
                ? $"Zorunlu bir alan boş gönderildi: {fieldLabel}. "
                  + "TRƏS bu alanı dolu istiyor; Pusula'daki ilgili bilgi eksik olabilir."
                : $"'{fieldLabel}' alanında beklenenden fazla değer gönderilmiş -- TRƏS yalnızca bir tane kabul ediyor.";
        }

        // -- Desen uyusmazligi (orn. Practitioner.active) --
        //    Satir basina SABITLENMEMELI: mesaj "HTTP 400: Practitioner.active[0]: ..." diye
        //    geliyor, yani alan yolu basta degil. Ilk yazimda ^ vardi ve bu hata cevrilmeden
        //    ham Ingilizce olarak ekranda kaliyordu (gercek kayitlarla test edilince goruldu).
        var desen = System.Text.RegularExpressions.Regex.Match(
            s, @"([\w\.]+(?:\[\d+\])?):\s*Value does not match pattern '([^']*)'");
        if (desen.Success)
            return $"'{FriendlyFieldName(desen.Groups[1].Value)}' alanı TRƏS'in beklediği değerle uyuşmuyor "
                 + $"(beklenen: {desen.Groups[2].Value}). Pusula'daki kayıt ya da eşleştirme kontrol edilmeli.";

        // -- Ham 404 govdesi (sunucu JSON dondurebiliyor) --
        if (s.Contains("\"title\":\"Not Found\"") || s.Contains("\"status\":404"))
            return "Kayıt TRƏS'te bulunamadı. Daha önce silinmiş ya da hiç oluşturulmamış olabilir -- "
                 + "silme denemesiyse yapacak bir şey yok, gönderim denemesiyse tekrar gönderilmeli.";

        // -- Aciklamasiz HTTP kodu: sunucu sebep bildirmedi --
        var ciplak = System.Text.RegularExpressions.Regex.Match(s, @"^HTTP (\d{3})$");
        if (ciplak.Success)
        {
            var kod = ciplak.Groups[1].Value;
            return kod switch
            {
                "401" or "403" => "TRƏS kimlik doğrulaması reddetti -- Ayarlar sayfasındaki kullanıcı/parola bilgileri kontrol edilmeli.",
                "404" => "Kayıt TRƏS'te bulunamadı (daha önce silinmiş olabilir).",
                "409" => "TRƏS bu kaydı çakışma nedeniyle reddetti -- genellikle başka kayıtlar ona bağlı olduğu için.",
                "500" or "502" or "503" => "TRƏS sunucusunda bir hata oluştu. Bizim gönderdiğimiz veride değil, karşı tarafta bir sorun var -- tekrar denenmeli.",
                _ => $"TRƏS isteği HTTP {kod} ile reddetti ama bir açıklama döndürmedi. Kayıt detayındaki sunucu yanıtına bakılmalı.",
            };
        }

        var (label, _) = ErrorCategory(s);
        return label switch
        {
            "FIN formatı hatalı" => "Kimlik/FIN numarası AZ FIN biçimine uymuyor -- Pusula'daki kayıt kontrol edilmeli.",
            "ICD tanı eksik/geçersiz" => "Protokolde geçerli bir ICD-10 tanı kodu yok -- Pusula'da tanı girilmeli.",
            "Zaman aşımı / bağlantı" => "TRƏS sunucusu zamanında yanıt vermedi -- bağlantı sorunu olabilir, tekrar denenmeli.",
            "TRƏS bağlantı ayarı eksik" => "Ayarlar sayfasında TRƏS bağlantı bilgileri eksik ya da hatalı.",
            "Referans bulunamadı" => "Bağlı bir kayıt TRƏS'te artık mevcut değil -- önce o kayıt tekrar gönderilmeli.",
            // Yeniden gondermek BU DURUMDA ISE YARAMAZ -- eksik olan Pusula'daki tanim.
            // Mesaj bu yuzden "tekrar gönder" demiyor, nereye bakilacagini soyluyor.
            "Eşleştirme eksik" => "Pusula'da tanım eksik: tetkikin/hizmetin İcbari Sigorta Fiyat Listesi karşılığı ya da LOINC kodu yok. Tanım tamamlanınca kayıt kendiliğinden gönderilir -- yeniden denemek işe yaramaz.",
            _ => s,
        };
    }

    // FHIR alan yolunu ("Observation.value[x].unit" gibi) hastane IT personelinin anlayacagi
    // bir Turkce etikete cevirir -- teknik yolu da parantez icinde SAKLAR (bilgi kaybetmemek
    // icin), sadece taniyamadigi bir alan icin ham yolu oldugu gibi doner.
    private static string FriendlyFieldName(string fieldPath)
    {
        // "[0]" gibi dizi indisleri temizleniyor -- yoksa "active[0]" eslesemiyor ve
        // kullaniciya "Practitioner.active[0]" gibi ham bir yol gosteriliyordu.
        var lastSegment = System.Text.RegularExpressions.Regex
            .Replace(fieldPath.Split('.')[^1].Split(':')[^1], @"\[\d+\]$", "");
        var label = lastSegment switch
        {
            "active" => "kaydın aktiflik durumu",
            "name" => "ad soyad",
            "birthDate" => "doğum tarihi",
            "gender" => "cinsiyet",
            "performer" => "işlemi yapan",
            "performed" => "işlem tarihi",
            "performedDateTime" => "işlem tarihi",
            "effectiveDateTime" => "sonuç tarihi",
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
