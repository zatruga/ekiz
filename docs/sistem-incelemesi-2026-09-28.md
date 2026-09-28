# Baştan uca sistem incelemesi -- 2026-09-28

Hata ve performans odaklı tam inceleme. 13.774 satır C#, 4 proje. Derleme temiz
(0 hata; 6 uyarının hepsi zararsız: Updater'ın Windows'a özgü olması ve
kullanılmayan bir NuGet paketi).

Bulgular iki gruba ayrıldı: **düzeltilenler** (kod değişti, aşağıdaki commit'ler)
ve **karar bekleyenler** (etkisi ölçüldü ama değişiklik bilinçli olarak
yapılmadı -- tasarım kararı gerektiriyor).

---

## Düzeltilenler

### 1. SQLite WAL kapalıydı -- iki süreç aynı dosyayı paylaşıyor

`synclog.db` dosyasını **iki ayrı süreç** açıyor: IIS'teki Web uygulaması
(içinde `AutoSyncWorker` da var) ve Windows servisi olarak kurulan
`PusulaSyncWorker` (bkz. `sunucu-kurulumu.md`). Veritabanı ise SQLite'ın
varsayılan `journal_mode=delete` modundaydı.

Bu modda bir yazar veritabanının tamamını kilitler. `Microsoft.Data.Sqlite`
açılışta `busy_timeout`'u `Default Timeout` değerinden (varsayılan 30 sn)
ayarladığı için belirti **hata değil BEKLEME** olurdu: otomatik gönderim turu
yazarken açılan bir sayfa 30 saniyeye kadar donar. Hata günlüğe düşmez, kullanıcı
yalnızca "sistem yavaş" der -- teşhisi zor bir sınıf.

WAL bu ilişkiyi kaldırır: okuyucular yazarı, yazar okuyucuları bloke etmez.
Artık tüm depolar `SqliteDb.ConnectionString` üzerinden açılıyor ve dosya bir
kez WAL'a alınıyor (ayar dosyaya yazılır, kalıcıdır). Doğrulandı:
`journal_mode=wal`, `-wal`/`-shm` yan dosyaları oluştu.

### 2. e-Health token'ı her istemci örneğine ayrıydı

`EHealthClient`, `AddHttpClient<EHealthClient>()` ile kaydedildiği için
**transient**. Token da onun kendi alanındaydı. Sonuçları:

- 13 senkron servisi kendi örneğini yakaladığı için **13 ayrı token** alıyordu
- İstemciyi kullanan **her sayfa açılışı** bakanlığa yeni bir `/auth/token`
  POST'u atıyordu -- her sayfa yüklemesine fazladan bir ağ gidiş-dönüşü
- Token alanları kilitsizdi: aynı anda iki istek 401 alırsa ikisi de yeniden
  kimlik doğruluyor, arada kalan üçüncü istek `null` token ile gidip yine 401
  alabiliyordu. Otomatik döngü ile arayüz aynı anda çalıştığında gerçek bir
  yarış durumu.

Token artık singleton `EHealthTokenCache`'te; yenileme tek seferde yapılıyor
(bayat token karşılaştırmasıyla, eş zamanlı 401'ler tek çağrıya düşüyor).

### 3. `GetLatestByPusulaIdsAsync` tabloyu iki kez tarıyordu

Id listesi sorguda iki kez kullanılıyordu (dış `WHERE` ve `MAX(Id)` alt
sorgusu) -- hem iki tarama hem iki kat parametre. Bu yüzden parti boyutu
SQLite'ın 999 değişken sınırına takılmamak için 400'de tutulmuştu.

SQLite'ın belgelenmiş davranışına geçildi: tek aggregate olarak `max()`
kullanıldığında `GROUP BY`'daki çıplak kolonlar max'ın geldiği satırdan gelir.
Tek geçiş, parametre sayısı id sayısına eşit, parti boyutu 900.

**Doğrulandı:** yeni sorgu ile eski sorgu, tüm kaynak tiplerinde 384 kayıtta
birebir aynı sonucu verdi (0 fark).

### 4. N+1 sorgular ve gereksiz ağır kolon okumaları

- `ProtocolFullSyncService`'te radyoloji ve patoloji döngüleri rapor başına
  `Procedure`/`Practitioner` sorgusu açıyordu. Toplu okumaya çevrildi.
- Bekleyen iş taraması artık `RequestJson`/`ResponseJson` kolonlarını hiç
  okumuyor (yalnızca duruma bakıyor).
- **Ana Sayfa her açılışında** bugüne kadar gönderilmiş TÜM Encounter'ları FHIR
  gövdeleriyle birlikte okuyordu; o ekran gövdeleri hiç kullanmıyor.
- `SyncLog.CreatedAtUtc` indeksi yoktu -- Aktivite sayfası ve Genel Bakış
  grafiği tarih aralığıyla filtreliyor, her filtre tabloyu baştan tarıyordu.

### 5. Otomatik gönderimde devre kesici yoktu

Bakanlık sunucusu ulaşılamaz olduğunda 50 protokolün her biri sırayla ağ zaman
aşımına düşüyordu (varsayılan 100 sn). Bir tur 80 dakikayı aşıp saatlik aralığa
taşabilirdi ve kapalı sunucuya düzenli kimlik doğrulama denemesi gidiyordu.

Artık üst üste **5 istisna** olursa tur bırakılıyor. Ayrım bilinçli: normal
başarısızlık (örn. bakanlığın reddettiği bir ICD-10 kodu) sayılmaz -- o protokole
özgüdür, diğerleri gönderilmeye devam etmeli. Yalnızca altyapı istisnaları
sayılır.

### 6. Ayar kapısı toplu/otomatik gönderimde atlanıyordu

`RadiologyReport.SendEnabled` ve `PathologyReport.SendEnabled` anahtarları
**yalnızca** `EncounterSyncService` cascade'inde denetleniyordu.
`ProtocolFullSyncService` -- yani toplu gönderim ve otomatik döngü -- bu
anahtarları hiç okumuyordu.

Etkisi bugün sıfır: iki anahtar Ayarlar ekranında gösterilmiyor, yani hep
varsayılan `true`. Ama otomatik gönderim bu servisten geçeceği için kapı burada
da kuruldu.

### 7. Tarih ayrıştırma kültüre bağlıydı

Sunucu tr-TR kültürüyle çalışıyor (kısa tarih kalıbı `d.MM.yyyy`). SyncLog ve
Users tablolarındaki ISO tarihleri kültür belirtilmeden ayrıştırılıyordu.

**Ölçüldü: mevcut veride hata YOK.** `CreatedAtUtc` her zaman
`DateTime.UtcNow`'dan geldiği için `"O"` biçimi sonuna `Z` koyuyor;
`RecordOpenedAt` ise soneksiz saklanıp `ToUniversalTime`'dan geçirilmiyor.

Ama doğruluk tesadüfe dayalıydı: soneksiz bir değer `Kind=Unspecified` okunur ve
`ToUniversalTime` onu yerel sayıp **4 saat kaydırır** --
`GetDailyTrendAsync`'te bir kez yaşanan hatanın aynısı. Artık
`InvariantCulture` + `DateTimeStyles.RoundtripKind` ile niyet kodda açık.

### Temiz çıkanlar

Şunlar arandı, sorun bulunmadı: sync-over-async (`.Result`/`.Wait()`),
`async void`, yutulmuş `catch` blokları, kültüre duyarlı sayı ayrıştırma
(hepsi zaten `InvariantCulture` kullanıyor). `EncounterSyncService` zaten doğru
toplu okuma yapıyor.

---

## Karar bekleyenler

Bu iki madde **ölçüldü ama değiştirilmedi** -- ikisi de sizin tasarım kararınızı
gerektiriyor ve ikisi de **otomatik gönderim açılmadan önce** cevaplanmalı.

### A. Bekleyen iş taraması gerçekte 850.000 satır çekiyor

`PendingWorkService.DefaultScanDays = 60`. Kodun kendi yorumu taramanın
"~15 sn sürdüğünü, 54.000 laboratuvar + 24.000 işlem adayı tarandığını"
söylüyor -- ama o ölçüm **7 günlük** pencereye ait. Canlı veride ölçüldü
(2026-09-28):

| Kaynak | 7 gün | 60 gün (gerçek varsayılan) |
|---|---:|---:|
| İşlem | 59.843 | **426.539** |
| Laboratuvar | 56.120 | **405.396** |
| Radyoloji | 1.394 | 11.278 |
| Epikriz | 618 | 5.995 |
| Patoloji | 173 | 1.119 |
| **Toplam** | 118.148 | **850.327** |

Yani gerçek tarama, belgelenenden **~7 kat** büyük. Bu 850.000 satır belleğe
`PendingCandidate` nesnesi olarak alınıyor, gruplanıyor, sonra ~950 parçalı
SyncLog sorgusuyla karşılaştırılıyor. Otomatik gönderim açıkken bu **her saat**
tekrarlanıyor; ayrıca Bekleyen İşler sayfasının her yenilenişinde.

**Neden kendi başıma değiştirmedim:** pencereyi kısaltmak tasarımın temel
özelliğini bozar. `PendingWorkService`'in kendi notu: *"kriter GÖNDERİLMEMİŞ
OLMAK (durum), bugün sonuçlanmış olmak (olay) DEĞİL... Tarih SADECE taramayı
sınırlar, karar kriteri değildir."* Pencere 14 güne inerse, 14 günden uzun
süredir gönderilememiş bir laboratuvar sonucu listeden **kalıcı olarak düşer** --
sistemin kendini onarma özelliği kaybolur.

**Seçenekler:**

1. **Kaynak başına farklı pencere.** 60 gün patoloji için konmuş
   (immünohistokimya haftalar sürebiliyor) ve patoloji 60 günde yalnızca 1.119
   satır. Asıl yükü laboratuvar ve işlem yapıyor, ikisi de aynı gün
   sonuçlanıyor. Patoloji/radyoloji/epikrizde 60 gün kalır, laboratuvar ve
   işlemde daha kısa bir pencere kullanılır. *Kendini onarma özelliği laboratuvar
   ve işlem için zayıflar.*
2. **Pencereyi koru, sıklığı düşür.** Tam tarama saatte bir değil günde bir
   (örn. gece) yapılır; saatlik turlar yalnızca kısa pencereye bakar.
   *Kendini onarma korunur, gecikme artar.*
3. **Olduğu gibi bırak.** 850.000 satır/saat kabul edilir. *En güvenli davranış,
   en yüksek maliyet.*

Tercihim **2** -- tasarımın özünü koruyup maliyeti 24'e bölüyor.

### B. Hastane veritabanına kilit hinti olmadan gidiliyor

Kod tabanının tamamında `NOLOCK`, `READ UNCOMMITTED` veya herhangi bir
izolasyon seviyesi ayarı **yok**. Yani Pusula'ya giden tüm sorgular SQL
Server'ın varsayılan `READ COMMITTED` seviyesinde çalışıyor ve paylaşımlı (S)
kilit alıyor.

Madde A ile birleşince tablo şu: **canlı OLTP hastane veritabanında, saatte bir,
850.000 satırlık sınırsız bir tarama, paylaşımlı kilitlerle.** Bu hem hastanenin
yazma işlemlerini bekletebilir hem de bizim sorgularımız hastane yazmalarına
takılıp yavaşlayabilir.

**Neden kendi başıma değiştirmedim:** `READ UNCOMMITTED`'a geçmek kirli okuma
riski getirir. Henüz commit edilmemiş, sonradan geri alınacak bir laboratuvar
sonucunu okuyup bakanlığa gönderirsek, hiç var olmamış bir sonucu resmî sağlık
kaydına yazmış oluruz. Sorgularımız "onaylı" durumları filtrelediği için bu
nadir, ama sıfır değil ve tespiti zor.

**Seçenekler:**

1. **Hastane DBA'sinden `READ_COMMITTED_SNAPSHOT` istenmesi.** Doğru çözüm:
   kilitsiz okuma + tam tutarlılık. Veritabanı seviyesinde bir ayar, bizim
   yetkimizde değil. *Kirli okuma riski yok.*
2. **Salt-okunur entegrasyon için `READ UNCOMMITTED`.** Yaygın pratik, tek
   satırlık değişiklik. *Kirli okuma riskini kabul etmek demek.*
3. **Olduğu gibi bırak, `LOCK_TIMEOUT` ekle.** Hastaneyi korumaz ama bizim
   sorgularımızın sonsuza kadar beklemesini engeller.

Tercihim **1**, olmazsa **3**. **2**'yi sağlık kaydı yazdığımız için önermiyorum.

---

## Ayrıca fark edilenler (düşük öncelik)

- `PusulaSyncWorker` Windows servisi olarak kuruluyor ama `Worker.cs` tek geçişli
  bir test aracı: işini yapıp `lifetime.StopApplication()` çağırıyor. Servis
  başlatılınca bir kez çalışıp duruyor; SCM bunu "durdu" olarak görür. Zararsız
  (otomatik gönderim Web sürecinde) ama kafa karıştırıcı.
- `AutoSyncWorker` her turda `services.CreateScope()` açıyor, ama tüm servisler
  singleton kayıtlı -- scope pratikte hiçbir şey yapmıyor.
- `HttpClient` zaman aşımı varsayılan 100 sn. Toplu gönderim için uzun; ama
  kısaltmak büyük gövdelerde başarısızlığa yol açabileceği için dokunulmadı.
