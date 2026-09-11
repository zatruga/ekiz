# PACS Erişim Talebi -- AZ e-Health `ImagingStudy` için

**Amaç:** Azerbaycan e-Health'e `az-imaging-study` kaynağı gönderebilmek. Profil iki
kimliği de **zorunlu** kılıyor ve bunlardan biri Pusula'da yok.

**Tarih:** 2026-09-11 · **Durum:** PACS ekibinden erişim bekleniyor

---

## Neden PACS'a ihtiyaç var

`az-imaging-study` profilinin zorunlu alanları ve kaynakları:

| Alan | Kardinalite | Kaynak | Durum |
|---|---|---|---|
| `identifier` (ACSN -- Accession Number) | 1..1 | Pusula | ✅ **Var** |
| `identifier` (UID -- Study Instance UID, `urn:dicom:uid`) | 1..1 | **PACS** | ❌ Pusula'da yok |
| `modality` | 1..* | **PACS** | ❌ Pusula kolonu boş |
| `subject` (Patient) | 1..1 | Pusula | ✅ Var |
| `extension:local-system-unique-id` | 1..1 | Kendimiz üretiyoruz | ✅ |
| `numberOfInstances` | 0..1 | Pusula (`GoruntuSayisi`) | ✅ %79 dolu |
| `encounter`, `started` | 0..1 | Pusula | ✅ Var |

Yani PACS'tan yalnızca **iki şey** gerekiyor: **Study Instance UID** ve **modalite**.

---

## Eşleştirme anahtarı çözüldü: Accession Number

Pusula, PACS'a gönderdiği HL7 `ORM^O01` siparişinde `OBR-2` (Placer Order Number)
alanına şu formatta bir numara yazıyor:

```
OBR|1|BAK393439||199111^MR, beyin, kontrastsiz^MR|...
```

Bu numara Pusula'da şöyle üretiliyor (canlı veride doğrulandı, 2026-09-11):

```
AccessionNumber = RIS.TetkikIslem.AccessionNoEkKarakter + RIS.TetkikIslem.Id
                = "BAK" + 393439
                = "BAK393439"
```

`AccessionNoEkKarakter` 270.074 kayıtta dolu ve tek değer: **`BAK`**.
(`AccessionNo` kolonu boş görünüyor -- Pusula bu numarayı saklamıyor, gönderirken
anlık birleştiriyor. Biz de aynı şekilde üretebiliyoruz.)

**Sonuç:** Accession Number'ı Pusula'dan üretebiliyoruz; PACS'a bununla sorup
Study Instance UID'yi alabiliriz. Bulanık eşleştirmeye (hasta + tarih + modalite)
gerek yok.

---

## Mevcut durum: elimizde ne var, ne yok

**Bilinen görüntüleyici entegrasyonu (Pusula'da tanımlı):**

```
http://10.10.204.195:8080/launch?action=search
    &username=authtoken&password=#Token#&PatientID=#HastaTCKimlikNo#
```

Bu bir **görüntüleyici açma (viewer launch) bağlantısı** -- kullanıcı için PACS
arayüzünü hasta filtreli açar (IHE "Invoke Image Display" kalıbı). **Veri sorgulama
API'si değil:** insana yönelik bir ekran döndürür, makine tarafından okunabilir
Study Instance UID vermez. Dolayısıyla `ImagingStudy` üretmek için tek başına
yeterli değil.

**Sunucu incelendi (2026-09-11):**

| Kontrol | Sonuç |
|---|---|
| `10.10.204.195:8080` erişilebilir mi | ✅ Evet (HTTP 302 → `/pureweb/loginRedirect.jsp`) |
| Ürün | **Fujifilm Synapse Mobility** (sayfa başlığı: "Synapse Mobility Login") |
| `/dicom-web/studies`, `/qido-rs/studies`, `/wado`, `/dicomweb/studies` | ❌ Hepsi HTTP 404 |

Yani sunucu ayakta ve görüntüleyiciyi sunuyor, ama **DICOMweb bu port üzerinde
standart yollarda açık değil**. `Ortak.Hl7Mesaj`'daki `FUJIPACSMP` etiketi de bu
ürünle örtüşüyor.

**Bu yüzden asıl talebimiz aşağıdaki gibi:** Synapse Mobility'nin görüntüleyici
bağlantısı değil, **Synapse arşivine sorgu erişimi**.

## Pusula PACS ayarlarından çıkan bağlantı noktaları (2026-09-11)

Pusula'nın PACS parametre ekranından alınan değerler ve **bu makineden yapılan
erişilebilirlik testleri**:

| Ayar | Değer | Test sonucu |
|---|---|---|
| Pacs Entegrasyon Tipi | `FUJIPACSMP` | -- |
| **Pacs Host IP / Port** | **`10.10.204.194` / `6600`** | ✅ **Port AÇIK** |
| Fuji Pacs Rapor Host / Port | `10.10.204.194` / `6610` | ✅ Port açık |
| Pacs Listener IP / Port | `10.10.201.72` / `8090` | ✅ Port açık |
| Pacs Link (görüntüleyici) | `http://10.10.204.195:8080/launch?...` | ✅ Synapse Mobility |
| **Radyolog PacsViewerLink** | **`http://bakfujisyn71/Synapse/WebQuery/index?path=/HBYSTETKIK/AccessionNumber=`** | ✅ Host ayakta (302 / 401) |
| Pacs Viewer Link Açılırken AccNo Kullanılsın | `True` | -- |
| Pacs DB Adı | `[PACS]` | PusulaHBYS'nin SQL sunucusunda böyle bir veritabanı YOK -- PACS makinesinde olmalı |

**En önemli doğrulama:** `Radyolog PacsViewerLink` ayarı Synapse'i **AccessionNumber
ile** sorguluyor (`.../WebQuery/index?path=/HBYSTETKIK/AccessionNumber=`). Bu, bizim
ürettiğimiz `BAK` + `TetkikIslem.Id` numarasının **Synapse tarafında da anahtar
olduğunu** doğruluyor -- eşleştirme varsayımımız sağlam.

**Buradan çıkan sonuç:** `10.10.204.194:6600` büyük olasılıkla Synapse'in DICOM
(DIMSE) uç noktası ve **bu ağdan erişilebilir durumda**. Yani C-FIND için ağ/güvenlik
duvarı işi muhtemelen zaten hazır; eksik olan tek şey **AE Title tanımı**.

## Denenen DICOM uç noktaları ve sonuçları (2026-09-11)

Aşağıdakiler **fiilen test edildi** (C-ECHO ve C-FIND; yalnızca doğrulama ve sorgu,
hiçbir veri değiştirilmedi). Calling AE olarak `PUSULA_EHEALTH` kullanıldı.

| Uç nokta | Ağ | C-ECHO | C-FIND |
|---|---|---|---|
| `10.10.204.191:104` AE=`bakmedscp` | ✅ 2 ms | ✅ **Success** | ❌ `a700 Out of Resources -> CFIND:SCP ExecuteDBCmd failed` |
| `10.10.204.191:104` AE=`bakmedmwl` | ✅ 2 ms | ✅ **Success** | ❌ Aynı hata (Modality Worklist sorgusunda da) |
| `10.10.204.200:104` AE=`HSTROKE` | ❌ ping yok, port kapalı | -- | -- |
| `10.10.204.194:6600` (Synapse) | ✅ port açık | ❌ AE Title bilinmiyor | -- |

**`10.10.204.191` hakkında:** Birliktelik (association) kuruluyor, **bizim AE Title'ımız
sorunsuz kabul ediliyor** (beyaz liste gerekmedi) ve SOP sınıfları müzakere ediliyor
(hem *Study Root Query/Retrieve - FIND* hem *Modality Worklist - FIND* kabul edildi).
Ama **her sorgu** sunucu tarafında `ExecuteDBCmd failed` ile düşüyor -- sorgu anahtarı
ne olursa olsun (AccessionNumber, PatientID, StudyDate, boş sorgu) ve sorgu tipi ne
olursa olsun. Kendi asıl işi olan iş listesi sorgusu bile başarısız.

İki olası açıklama, ikisi de PACS ekibine sorulmalı:
1. Bu ağ geçidinin veritabanı bağlantısı bozuk (hastanenin haberi olmayan canlı bir
   arıza olabilir -- iş listesi de çalışmıyor demektir),
2. Ya da `PUSULA_EHEALTH` AE'si tanımlı olmadığı için ürün yetki hatasını veritabanı
   hatası olarak raporluyor (bazı ürünler böyle yapar).

**`10.10.204.200` (HSTROKE):** Bu makineden hiç erişilemiyor -- ping yanıtı yok, 104
portu kapalı. Kapalı, başka bir ağ segmentinde ya da güvenlik duvarıyla ayrılmış olabilir.

**Sonuç:** Test edilen iki uç nokta da Study Instance UID vermiyor. İhtiyacımız olan
arşiv **Synapse** (`10.10.204.194:6600`) -- portu açık ama **AE Title'ını bilmediğimiz
için** birliktelik kurulamıyor.

### Synapse Mobility'de bir API var (2026-09-11)

`http://10.10.204.195:8080` üzerinde yol taraması yapıldı:

| Yol | Yanıt | Anlamı |
|---|---|---|
| `/viewer`, `/viewer/` | 302 → `/pureweb/server/login.jsp` | Görüntüleyici, giriş istiyor |
| **`/viewer/api`** | **302 → login** | **Bu yol VAR, sadece kimlik doğrulama arkasında** |
| `/api`, `/rest`, `/services`, `/pureweb` | 404 | Yok |

`/viewer/api`'nin 404 değil **302** dönmesi önemli: yol mevcut, yalnızca oturum
gerekiyor. Yani Synapse Mobility'nin bir API yüzeyi var.

Kimlik doğrulama mekanizması Pusula'nın launch bağlantısından okunabiliyor:
`username=authtoken&password=#Token#` -- yani token tabanlı. (Pusula ayarlarındaki
`Pacs Viewer Link UserName` / `UserPassword` alanları boş bırakılmış, token yolu
kullanılıyor.)

**Bu API'yi kurcalamaya çalışmadık** -- token üretimini tersine mühendislikle
çözmek yerine PACS ekibinden usulüne uygun kimlik bilgisi istemek doğrusu.

> ### 🔑 İki somut talep
>
> 1. **Synapse'in AE Title'ı** -- `10.10.204.194:6600` portu açık, C-FIND denemesi
>    hemen yapılabilir (görüntü indirme gerekmiyor, yalnızca sorgu).
> 2. **Synapse Mobility API kimlik bilgisi** -- `/viewer/api` mevcut; servis hesabı
>    ya da token üretme yöntemi. (1. madde çalışırsa buna gerek kalmayabilir.)

> ### Eski not: tek kalan engel Synapse'in AE Title'ı
>
> Ağ açık, eşleştirme anahtarı (`BAK`+`TetkikIslem.Id`) iki kaynaktan teyitli, görüntü
> indirme gerekmiyor. Synapse'in AE Title'ı öğrenilince C-FIND denemesi hemen yapılabilir.

## PACS ekibinden istenenler

### 1. Tercih edilen: DICOMweb (QIDO-RS) okuma erişimi

Modern ve en kolay yol. Gereken:

- **Base URL** (örn. `https://pacs.hastane.local/dicom-web`)
- **Kimlik doğrulama yöntemi**: Basic / Bearer token / mTLS -- hangisiyse, kullanıcı
  ve parola veya sertifika
- Erişecek sunucunun beyaz listeye alınması: **uygulama sunucusu `10.10.201.57`**
- Güvenlik duvarı kuralı (giden HTTPS)

Yapacağımız tek sorgu tipi:

```http
GET {base}/studies?AccessionNumber=BAK393439
     &includefield=0020000D      # StudyInstanceUID
     &includefield=00080061      # ModalitiesInStudy
     &includefield=00201206      # NumberOfStudyRelatedSeries
     &includefield=00201208      # NumberOfStudyRelatedInstances
Accept: application/dicom+json
```

**Sadece okuma yeterli.** `WADO-RS` (görüntü indirme) veya `STOW-RS` (yazma)
istemiyoruz -- görüntülerin kendisini taşımıyoruz.

> **Bu, profilden doğrulanmış bir sınırdır, tercih değil:** `az-imaging-study`
> yalnızca meta veri taşır. `endpoint` elemanı, `Binary`/`Attachment`/`Media` ya da
> WADO adresi İÇERMEZ (IG'den okundu, 2026-09-11). Görüntülerin kendisini e-Health'e
> göndermenin profilde bir yolu yok; kaynak bir DICOM çalışma KAYDI. Dolayısıyla
> PACS'tan piksel veri çekmemiz hiçbir senaryoda gerekmiyor.

### 2. Muhtemelen en hızlı yol: DIMSE C-FIND

`10.10.204.194:6600` **zaten açık ve bu ağdan erişilebilir** (test edildi). Bu
yüzden en küçük talep bu olabilir:

- **Bizim AE Title'ımızın Synapse'te tanımlanması** (örn. `PUSULA_EHEALTH`) --
  muhtemelen tek gereken iş
- Synapse'in **AE Title**'ı ve doğru sorgu portunun teyidi (6600 mü?)
- Yalnızca **C-FIND** yetkisi yeterli; C-MOVE/C-GET gerekmiyor

Sorgu: *Study Root Query/Retrieve Information Model – FIND*, anahtar
`AccessionNumber`, istenen alanlar `StudyInstanceUID`, `ModalitiesInStudy`.

### 3. Synapse'e özel sorular

Ürün **Fujifilm Synapse Mobility** olarak tespit edildi. Sorulacaklar:

- Synapse'te **DICOMweb (QIDO-RS)** hizmeti açık mı? Açıksa hangi host/port/yol
  üzerinde? (8080'de standart yollarda bulunamadı -- ayrı bir servis, farklı port
  ya da lisans opsiyonu olabilir.)
- Açık değilse etkinleştirilmesi mümkün mü, yoksa **C-FIND** mi kullanmalıyız?
- Synapse Mobility'nin kendi REST arayüzü accession numarasıyla çalışma bilgisi
  (StudyInstanceUID) döndürebiliyor mu? Döndürüyorsa dokümantasyonu.
- Elimizdeki `launch?action=search...` bağlantısındaki `authtoken` kullanıcısı ve
  `#Token#` mekanizması sorgu erişimi için de kullanılabilir mi, yoksa ayrı bir
  servis hesabı mı açılmalı?

### 4. Hangi sistem sorgulanmalı?

Hastanede üç entegrasyon canlı görünüyor (`Ortak.Hl7Mesaj`, hepsinde bugün trafik):

| Entegrasyon | Mesaj sayısı |
|---|---:|
| `FUJIPACSMP` (Fuji PACS) | 768.148 |
| `TELETIP` | 298.547 |
| `VNA` (Vendor Neutral Archive) | 6.077 |

**Sorulacak:** Study Instance UID sorgusu için hangisi doğru uç nokta -- Fuji PACS mı,
VNA mı? Uzun vadeli arşiv VNA ise sorguyu oraya yöneltmek daha doğru olabilir.

### 5. Doğrulama için bir örnek

Erişim açıldığında test edebilmek için bilinen bir accession numarası yeterli.
Örnek (11.09.2026 tarihli, gerçek kayıtlar):

| Accession | Modalite | Görüntü sayısı |
|---|---|---:|
| `BAK392082` | MR, üst abdomen, kontrastlı | 2683 |
| `BAK393394` | MR, multiparametrik prostat | 148 |
| `BAK393413` | Akciğer P.A. (DX) | 1 |

---

## Erişim geldikten sonra bizde yapılacaklar

1. `PacsClient` (QIDO-RS ya da C-FIND) -- accession → Study Instance UID + modalite
2. `ImagingStudyMapper` -- `az-imaging-study` kaynağını üretir
3. `RadiologyReportSyncService` içine bağlanır: rapor gönderilirken ImagingStudy de
   gönderilir, `DiagnosticReport.imagingStudy` ile ilişkilendirilir
4. UID bulunamayan tetkikler **atlanır** (uydurma UID gönderilmez) ve senkron
   günlüğüne nedeni yazılır -- projedeki diğer zorunlu alan kurallarıyla aynı ilke

**Not:** Bakanlığa "ImagingStudy bizden bekleniyor mu, bekleniyorsa görüntülerin
kendisi mi yoksa yalnızca çalışma referansı mı?" sorusu da soruldu
(bkz. `docs/bakanlik-sorulari.md`). Cevap "görüntüler de gönderilecek" olursa bu
talebin kapsamı genişler (WADO-RS + muhtemelen `Binary`/`Endpoint` kaynakları).
