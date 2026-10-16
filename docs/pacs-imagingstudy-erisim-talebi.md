# PACS Erişim Talebi -- AZ e-Health `ImagingStudy` için

**Amaç:** Azerbaycan e-Health'e `az-imaging-study` kaynağı gönderebilmek. Profil iki
kimliği de **zorunlu** kılıyor ve bunlardan biri Pusula'da yok.

**Tarih:** 2026-09-11 · **Son güncelleme:** 2026-10-16 · **Durum:** PACS ekibinden erişim bekleniyor

> ### 16.10.2026 — talep **tek alana** indi
>
> Bu tarihte yapılan ölçümler iki şeyi değiştirdi:
>
> 1. **Modality artık Pusula'dan geliyor.** `RIS.Tetkik.PACSModaliteTipiId` **%100
>    dolu** ve zaten DICOM kodu taşıyor (US, CR, CT, MR, XA, MG, BMD, ECG, OT).
>    PACS'tan modalite **istemiyoruz** — talep ikiye değil **bire** indi.
> 2. **UID'nin Pusula'da olmadığı artık kanıtlı** (aşağıdaki bölüm). Önceki
>    "bulunamadı" ifadesi kolon adı taramasına dayanıyordu; bu zayıf kanıttı.
>
> **Tek istenen:** accession numarası verilince **Study Instance UID** döndüren
> salt-okunur bir sorgu.

> **KAPSAM KARARI (kullanıcı, 2026-09-14):** *"Radyoloji sadece study/imaj bilgisi,
> görüntü yok."* Yani e-Health'e yalnızca `ImagingStudy` **meta verisi** gidecek --
> DICOM piksel verisi hiçbir şekilde taşınmayacak. Bu karar profilin kendisiyle de
> örtüşüyor (`az-imaging-study`'de `endpoint`/`Binary`/`Attachment` yok).
>
> **Uygulama sonucu:** PACS'tan istenecek yetki **yalnızca sorgu** (C-FIND / QIDO-RS).
> C-MOVE, C-GET, WADO-RS **hiçbir zaman** istenmeyecek.

---

## Neden PACS'a ihtiyaç var

`az-imaging-study` profilinin zorunlu alanları ve kaynakları:

| Alan | Kardinalite | Kaynak | Durum |
|---|---|---|---|
| `identifier` (ACSN -- Accession Number) | 1..1 | Pusula | ✅ **Var** |
| `identifier` (UID -- Study Instance UID, `urn:dicom:uid`) | 1..1 | **PACS** | ❌ **Tek eksik alan** |
| `modality` | 1..* | Pusula (`RIS.Tetkik.PACSModaliteTipiId`) | ✅ **%100 dolu** (16.10.2026) |
| `subject` (Patient) | 1..1 | Pusula | ✅ Var |
| `extension:local-system-unique-id` | 1..1 | Kendimiz üretiyoruz | ✅ |
| `numberOfInstances` | 0..1 | Pusula (`EMR.Radiology.Result.ImageCount`) | ✅ %66 dolu |
| `encounter`, `started` | 0..1 | Pusula | ✅ Var |

Yani PACS'tan **tek bir şey** gerekiyor: **Study Instance UID**.

### Modality nasıl çözüldü (16.10.2026)

Daha önce "Pusula kolonu boş" yazıyordu; yanlıştı. Pusula, PACS'a gönderdiği HL7
`ORM` mesajının `OBR-24` alanına modaliteyi **DICOM kodu olarak** yazıyor — yani
bu kodu bir yerde tutuyor. Kaynak: `RIS.Tetkik.PACSModaliteTipiId`.

01.10.2026 sonrası onaylı 3.257 radyoloji raporunda **tek bir boş değer yok**:

| Kod | Adet | DICOM'da durumu |
|---|---:|---|
| US · CR · CT · MR · XA · MG · BMD · ECG · OT | **2.812** | Geçerli — olduğu gibi gider |
| EC (ekokardiyografi) | 278 | DICOM'da *emekli* kod → `US` olarak gönderilecek |
| SFT · EEG · EMG · XX · PTA | 167 | DICOM'da yok → `OT` (Other) |

Yani 6 satırlık bir eşleştirme tablosuyla modality **%100 çözülüyor**.

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

**16.10.2026 teyidi — artık çıkarsama değil.** Canlı HL7 trafiğinden iki gerçek
mesaj okundu; ikisinde de accession numarası tam beklediğimiz biçimde:

| Mesaj | Hedef | `ORC-2` / `OBR-2` | `OBR-24` (modalite) |
|---|---|---|---|
| `ORM^O01` | `FUJIPACSMP` | `BAK404447` | `MR` |
| `ORU^R01` | `TELETIP` | `BAK404568` | `EC` |

`BAK` + `TetkikIslem.Id` formülü üç bağımsız kaynaktan doğrulandı: Pusula
veritabanı, Synapse viewer bağlantısı (`.../AccessionNumber=`) ve canlı HL7 mesajı.

---

## Study Instance UID Pusula'da YOK — bu artık kanıtlı (16.10.2026)

Önceki sürümde bu, **kolon adı taramasına** dayanıyordu. O yöntem bu veritabanında
güvenilmez: login bazı nesneleri katalog görünümlerinde göremiyor (`Ortak.Il`
örneği), ve tarama veritabanı öneki içermediği için **yalnızca `PusulaHBYS_BAK`'ı**
görmüştü. Üç katmanda yeniden ölçüldü:

### 1 · Altı veritabanının tamamı tarandı

Sunucudaki her kullanıcı veritabanı ayrı ayrı tarandı (`PusulaHBYS_BAK`,
`COMED.LIS_BAK`, `MERKEZDB`, `MERKEZ_COMED`, `PusulaFile_BAK`,
`PusulaFile_MERKEZDB`) ve arama `Uid`/`Study` ile sınırlı tutulmayıp `Dicom`,
`SOPInstance`, `SeriesUid`, `Accession`, `Pacs` kalıplarına genişletildi.

Çıkan 48 kolonun hiçbiri Study Instance UID değil. En yakın aday
`Aktarim.DicomGoruntuAktarim.DicomNo` idi — ama `nvarchar(30)` (bir UID'yi
tutamaz) ve **tablo boş**.

### 2 · PACS izi taşıyan beş tablonun hepsi boş

| Tablo | Satır |
|---|---:|
| `RIS.PACSWorkList` | 0 |
| `Aktarim.DicomGoruntuAktarim` | 0 |
| `Aktarim.TetkikIslemAktarim` | 0 |
| `RIS.TetkikIslemStmSorgulamaSonucu` | 0 |
| `EMR.Shared.DocumentForDeviceReportingLog` | 0 |

### 3 · Belirleyici kanıt: entegrasyon **tek yönlü**

`Ortak.Hl7Mesaj` tablosu PACS trafiğinin tamamını tutuyor — 2022'den bugüne
**194.619** `FUJIPACSMP` mesajı, bugün 15:21'e kadar canlı. 01.10.2026 sonrası
pencerede mesajların **gönderen uygulaması okundu**:

| `TransferTipi` | Gönderen | Mesaj tipi | Adet |
|---|---|---|---:|
| 1 | **PUSULA** | `ORM` (istek) | 3.696 |
| 1 | **PUSULA** | `ORU` (sonuç) | 3.472 |
| 0 | **PUSULA** | `ORM` (istek) | 2.118 |

**Hepsini Pusula gönderiyor.** `TransferTipi` 0 ve 1 iki ayrı giden kanal (biri
kuyruk, biri gönderilmiş); **gelen yön diye bir şey yok.** PACS'tan Pusula'ya tek
bir mesaj dönmüyor.

Aynı pencerede mesaj gövdeleri UID kalıplarına karşı tarandı:

| Aranan | Bulunan |
|---|---:|
| `ZDS` segmenti (Fujifilm UID taşıyıcısı) | 0 |
| `IPC` segmenti (HL7 v2.5.1 UID taşıyıcısı) | 0 |
| `1.2.` ile başlayan DICOM UID | 0 |
| `1.3.` ile başlayan DICOM UID | 0 |

**Mimari sonuç:** Study Instance UID'yi modalite, çalışma yapıldığı anda üretir ve
PACS'a yazar. Pusula bu bilgiyi hiç almıyor, çünkü **dönüş kanalı yok**. Yani UID
Pusula'da bulunamıyor değil — **bulunması mümkün değil.** PACS'a sormaktan başka
yol yoktur.

> Bunu kapatmanın iki yolu var; ikisi de PACS/Pusula tarafında bir iş:
>
> - **(tercih)** PACS'a salt-okunur sorgu yetkisi — aşağıdaki talep.
> - Ya da Pusula'nın PACS'tan **gelen** mesajları da işlemesi (MPPS / `ORU` ile
>   dönen UID'yi `RIS.TetkikIslem`'e yazması). Bu bir Pusula geliştirmesi olur ve
>   bizim kapsamımız dışında.

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

### Ek testler (2026-09-14) -- çalışan DICOM sorgu ucu YOK

**`10.10.204.194:6600` ve `:6610` DICOM DEĞİL.** 8 farklı called AE ile denendi
(SYNAPSE, FUJI, PACS, SYNAPSEPACS, FUJIPACS, BAKFUJISYN71, ARCHIVE, ANY-SCP) --
**hepsi sessiz**: ne birliktelik kabulü, ne DICOM reddi. DICOM konuşan bir port
yanlış AE'ye *red* döndürür; sessizlik protokolün DICOM olmadığını gösterir. Bunlar
Pusula'nın `ORM` mesajı gönderdiği **HL7 dinleyicileri**. Ayrıca `.194` ve `.195`
üzerinde yaygın DICOM portları (104, 2761, 2762, 4242, 11112, 8042) **kapalı**.

**`10.10.204.191`'deki hata yetkiyle ilgili değil.** `ExecuteDBCmd failed` hatasının
tanınmayan çağıran AE'den kaynaklanabileceği düşünülmüştü; 7 farklı çağıran AE ile
denendi (HBYSTETKIK, bakmedscp, PUSULA, HBYS, BAK, FUJIPACSMP, PUSULA_EHEALTH) --
**hepsi aynı hatayı** verdi. Yani SCP'nin sorgu veritabanı gerçekten çalışmıyor;
kendi asıl işi olan Modality Worklist sorgusu da dahil.

> ⚠️ **Bu, hastanenin haberi olmayan canlı bir arıza olabilir:** `bakmedmwl` iş listesi
> sağlayıcısı hiçbir sorguya cevap veremiyor. Modaliteler iş listesini bu uçtan
> alıyorsa onlar da etkileniyordur. PACS ekibine ayrıca bildirilmeli.

**Özet:** Ağda erişebildiğimiz **çalışan bir DICOM sorgu ucu yok**. Elimizdeki üç
uçtan biri bozuk, biri HL7, biri erişilemez.

> ### 🔑 Talep tek cümleye indi (16.10.2026)
>
> **"Accession numarası (`BAK` + numara) verildiğinde Study Instance UID döndüren
> salt-okunur bir sorgu."**
>
> Modalite artık Pusula'dan geliyor; PACS'tan **başka hiçbir alan istenmiyor.**
> Görüntü indirme (C-MOVE / C-GET / WADO-RS) hiçbir senaryoda gerekmiyor.
>
> Bunu sağlamanın üç yolundan **biri** yeterli:
>
> 1. **C-FIND yetkisi** — AE Title'ımızın (`PUSULA_EHEALTH`) Synapse'te tanımlanması
>    ve Synapse'in AE Title'ı + doğru sorgu portu. Standart DICOM hizmeti, Fujifilm
>    tarafında geliştirme gerektirmez.
> 2. **DICOMweb (QIDO-RS)** erişimi — base URL + servis hesabı.
> 3. **Synapse Mobility API** kimlik bilgisi — `/viewer/api` mevcut (302 dönüyor,
>    404 değil), token üretme yöntemi ya da servis hesabı.
>
> Üçü de olmazsa geriye tek seçenek kalır: Pusula'nın PACS'tan **gelen** mesajları
> işleyip UID'yi `RIS.TetkikIslem`'e yazması (yukarıdaki not).

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
     &includefield=0020000D      # StudyInstanceUID  <-- tek ihtiyacimiz bu
Accept: application/dicom+json
```

> **16.10.2026:** `ModalitiesInStudy` (00080061) artık istenmiyor — modalite
> Pusula'dan geliyor. `NumberOfStudyRelatedInstances` da gerekmiyor; görüntü
> sayısını `EMR.Radiology.Result.ImageCount` veriyor. Sorgunun döndürmesini
> istediğimiz alan **yalnızca `StudyInstanceUID`**.

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

| Entegrasyon | Mesaj sayısı | Yön |
|---|---:|---|
| `FUJIPACSMP` (Fuji PACS) | 787.889 | **tamamı giden** |
| `TELETIP` | 305.962 | giden |
| `VNA` (Vendor Neutral Archive) | 6.274 | giden |

(Sayımlar 16.10.2026, `Ortak.Hl7Mesaj`; üçünde de bugün trafik var.)

**Sorulacak:** Study Instance UID sorgusu için hangisi doğru uç nokta -- Fuji PACS mı,
VNA mı? Uzun vadeli arşiv VNA ise sorguyu oraya yöneltmek daha doğru olabilir.

**Ayrıca bildirilmeli:** üç entegrasyonun **hiçbirinde gelen mesaj yok**. Hastane
PACS'a istek ve rapor gönderiyor, karşılığında hiçbir şey almıyor. UID sorunumuzun
kökü bu; ama aynı zamanda PACS ekibinin bilmesi gereken genel bir durum.

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

1. `PacsClient` (QIDO-RS ya da C-FIND) -- accession → Study Instance UID
   (**yalnızca bu alan**; modalite PACS'tan sorulmuyor)
2. `AzModalite` -- `PACSModaliteTipiId` → DICOM modality eşleştirmesi
   (`EC`→`US`, `SFT`/`EEG`/`EMG`/`XX`/`PTA`→`OT`, gerisi olduğu gibi)
3. `ImagingStudyMapper` -- `az-imaging-study` kaynağını üretir
4. `RadiologyReportSyncService` içine bağlanır: rapor gönderilirken ImagingStudy de
   gönderilir, `DiagnosticReport.imagingStudy` ile ilişkilendirilir
5. UID bulunamayan tetkikler **atlanır** (uydurma UID gönderilmez) ve senkron
   günlüğüne nedeni yazılır -- projedeki diğer zorunlu alan kurallarıyla aynı ilke

**Not:** Bakanlığa "ImagingStudy bizden bekleniyor mu, bekleniyorsa görüntülerin
kendisi mi yoksa yalnızca çalışma referansı mı?" sorusu da soruldu
(bkz. `docs/bakanlik-sorulari.md`). Cevap "görüntüler de gönderilecek" olursa bu
talebin kapsamı genişler (WADO-RS + muhtemelen `Binary`/`Endpoint` kaynakları).
