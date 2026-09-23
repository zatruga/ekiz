# Bakanlık inceleme geri bildirimi — 2026-09-16

Bakanlık (e-Health) ekibi, gönderdiğimiz 23 örnek protokolü inceleyip 6 başlıkta
düzeltme istedi. Aşağıda her madde **koda ve canlı Pusula verisine karşı**
doğrulandı; hangisinin hemen yapılabileceği, hangisinin engelli olduğu ölçümle
belirtildi.

| # | Konu | Durum | Engel |
|---|---|---|---|
| 1 | Telefon formatı (`+994`) | ✅ **Yapıldı** (`ad093ef`) | — |
| 2 | Bölüm eşleştirmeleri (`Digər`) | **Bakanlık bekleniyor** | Terminoloji listesi güncellenecek |
| 3a | Tanı açıklaması Azerbaycanca | ✅ **Yapıldı** (`ba23eec`) | — |
| 3b | `diagnosis-type` | ✅ **Yapıldı** — protokollerin %85'inde | Kalan %15 kaynak veri eksiği (kullanıcı onayı: bu haliyle gönderilecek) |
| 3c | `first-diagnosis` | **Karar gerekiyor** | Pusula'da böyle bir alan yok |
| 3d | `verificationStatus` (ön/kesin tanı) | ✅ **Yapıldı** | — |
| 4 | `az-observation` (vital/not) | ✅ **Yapıldı** (vital) | Doktor notları zaten epikrizde — mükerrer olmasın diye gönderilmiyor |
| 5 | Laboratuvar `component` yapısı | ✅ **Yapıldı** (`e9f1908`) | — |
| 6 | ImagingStudy | **Engelli** | PACS'tan Study Instance UID alınamıyor |
| 7 | LOINC `display` | ✅ **Yapıldı** | Kaynak: tx.fhir.org (HL7 resmî), LOINC 2.82 |

---

## 1. Telefon formatı

**Şu an:** `PatientMapper` Pusula'daki değeri olduğu gibi gönderiyor
(`telecom.value = h.GSM`), hiçbir normalizasyon yok.

**Ölçüm (`hasta.hasta`, State<>0):**

| Biçim | Adet | Örnek |
|---|---:|---|
| `0` + 9 hane | 132.752 | `0505545497` |
| 9 hane, öneksiz | 112.727 | `505001713` |
| `0` + boşluklu | 3.310 | `050 500 09 41` |
| `994` ile | 454 | `994102040001` |
| **Çöp veri** | ~5.000 | `--------------`, `000000000`, `(567)34` |

İlk iki grup toplam **245.479 kayıt** ve ikisi de tek kurala oturuyor: boşluk/tire
temizle, baştaki `0`'ı at, `+994` ekle.

**Yapılacak:** `AzPhone.Normalize()` yardımcısı. Normalize edilemeyen (9 haneye
inmeyen, geçerli operatör önekiyle başlamayan) numara **hiç gönderilmez** --
`telecom` alanı `0..*` olduğu için bu profili bozmaz. Uydurma bir numara
göndermektense hiç göndermemek doğru; aynı ilke `PathologyFindingMapper`'daki
morfoloji biçim denetiminde de uygulandı.

---

## 2. Bölüm eşleştirmeleri

Bakanlık listeyi güncelleyecek, sonra yeniden eşleştireceğiz. Şu an **999
(Digər)** ile giden 12 bölüm ve son 365 gündeki kullanımları:

| Pusula bölümü | Kullanım |
|---|---:|
| Check Up | 3.644 |
| Histopatologiya | 1.761 |
| Plastik, Estetik və Rekonstruktiv Cərrahiyyə | 1.170 |
| İnvaziv Radyologiya | 1.148 |
| Evdə Tibbi xidmət | 681 |
| İşyeri həkimi | 527 |
| Kosmetoloq | 374 |
| Alqologiya | 156 |
| Kök hüceyrə | 95 |
| Palyatif Bakım Kliniği | 18 |
| Saç əkimi | 7 |
| xxx | 1 |

Bunların hiçbiri "eşleştirilmemiş" değil -- **hepsi bilinçli olarak 999 seçildi**,
çünkü AZ listesinde karşılığı yoktu. Liste güncellenince Bölüm Eşleştirme
sayfasından tek tek gözden geçirilecek. Bu 12 bölümü bakanlığa iletmek işi
hızlandırır.

**Not:** `xxx` adlı bölüm (1 kullanım) Pusula'da bir test kaydı gibi duruyor;
ilgili birime sorulmalı.

---

## 3. Tanı bilgileri

### 3a. Açıklama dili

**Sebep bulundu:** `ConditionMapper` `display` alanına Pusula'nın kendi ICD
tablosundaki adı (`Sube.Tedavi_ICD.Adi`) yazıyor ve o tablo **karışık** --
bir kısmı Azerbaycanca, bir kısmı Türkçe (`"Aterosklerotik kardiyovasküler
hastalık"`, `"EKLEMDE AĞRI, OMUZ BÖLGESİ"`).

**Çözüm hazır:** AZ `az-icd-10` CodeSystem'i (33.083 kod, `content: complete`)
indirildi. Artık `display` bu listeden, koda göre okunacak. Pusula'nın metni
yalnızca kod AZ listesinde yoksa yedek olarak kullanılacak -- ki o durumda zaten
kayıt reddediliyor (bkz. ICD-10 boşluğu maddesi).

Bu, açıklamayı **bakanlığın kendi terminolojisiyle birebir** aynı yapar.

### 3b. `diagnosis-type` — kısmi

Profilde `Condition.extension:diagnosis-type` **0..1**, `required` binding ile
`http://fhir.az/CodeSystem/diagnosis-type`'a bağlı:

| Kod | Anlam |
|---|---|
| 1 | Əsas diaqnoz |
| 2 | əlavə diaqnoz |
| 3 | Yanaşı xəstəliklər |

Bu bir **sıra** eksenidir: hangisi ana tanı, hangisi ek.

**İlk eşleştirmemiz yanlıştı ve düzeltildi (2026-09-18).** `IsBirincilTani = 1` →
kod 1 demiştik. Ölçtük, bu bayrak protokol başına **tek değil**:

| Protokoldeki tanı | `IsBirincilTani = 1` olan | Protokol |
|---|---|---:|
| 2 tanı | 2'sinde birden | 17.351 |
| 3 tanı | 3'ünde birden | 4.765 |
| 4 tanı | 4'ünde birden | 2.367 |

Yani 4 tanılı bir protokolde **4 tane "əsas diaqnoz"** gönderiyorduk — tanım
gereği imkânsız.

**Pusula'da bu eksenin kaynağı yok** (son 365 gün, 186.038 kayıt):

| Alan | Durum |
|---|---|
| `IsBirincilTani` | protokol başına tek değil (yukarıdaki tablo) |
| `IsEkTani` | %100 NULL |
| `SiraNo` | %100 NULL |
| `IsAnaTani` | `MedulaTaniTipiId = 2` ile birebir örtüşüyor — bağımsız bilgi değil |

**Kullanıcı kararı (iki kollu):**

1. Protokolde **tek** tanı varsa o tanı mantıksal zorunlulukla esas tanıdır → kod **1**.
2. Çok tanılı protokolde **kesin tanı** (`MedulaTaniTipiId = 2`) esas tanı sayılır → kod **1**.

Çok tanılı 29.503 protokolde 2. kuralın ürettiği sonuç:

| Kesin tanı sayısı | Protokol | Pay |
|---|---:|---:|
| Hiç yok (alan boş kalır) | 20.769 | %70 |
| Tam 1 (temiz sonuç) | 5.366 | %18 |
| Birden fazla | 3.368 | %11 |

Son satır birden fazla "Əsas diaqnoz" üretiyor. Bu, `IsBirincilTani` hatasından
farklı: orada bayrak neredeyse her tanıda 1'di (bilgi taşımıyordu), burada
gerçekten iki tanının da kesinleştiği klinik bir durum var. Tüm protokollerin
%2,4'ü; ölçülerek sunuldu ve kabul edildi.

**Ön tanı satırlarına kod 2 (əlavə diaqnoz) yazılmıyor.** "Ön tanı" kesinlikle
ilgili bir ifadedir, sırayla değil — bir ön tanı pekâlâ protokolün tek ve asıl
şüphesi olabilir; ona "ek tanı" demek uydurma bir sıra iddiası olurdu.

**Kapsam (son 365 gün, 138.006 protokol):**

| Grup | Protokol | Pay | `diagnosis-type` |
|---|---:|---:|---|
| Tek tanılı | 108.503 | %78,6 | kod 1 |
| Çok tanılı, kesin tanı var | 8.735 | %6,3 | kod 1 |
| Çok tanılı, kesin tanı yok | 20.768 | %15,1 | boş |

**Boş kalan %15 bir eşleştirme eksiği değil, kaynak veri eksiği.** O 20.768
protokoldeki 56.834 tanının **56.363'ü (%99,2) ön tanı** -- yani hekim birden
fazla tanı girmiş ama hiçbirini kesin tanıya çevirmemiş. Hangisinin esas olduğu
Pusula'da **kayıtlı değil**; birini seçmek veriyi okumak değil uydurmak olurdu.

Kapatmanın iki yolu var, ikisi de bizde değil:
1. **Hastane:** protokol kapatılırken bir tanının kesin tanıya çevrilmesi.
2. **Bakanlık:** "kesin tanı yoksa ilk ön tanı esas sayılsın" gibi bir kural onaylaması.

**Kullanıcı kararı (2026-09-18):** bu haliyle gönderilecek, madde tamamlandı sayılıyor.

### 3c. `first-diagnosis`

Profilde `0..1`, **boolean**, binding yok. Anlamı: "bu tanı hastaya **ilk kez**
mi konuldu".

**Pusula'da böyle bir alan YOK.** `IlkTaniTarihi` adında bir kolon var ama son
365 günde 186.038 kaydın yalnızca **99'unda** dolu — kullanılmıyor.

Hesaplayabiliriz: aynı hastanın geçmişinde aynı ICD kodu daha önce geçmiş mi
diye bakılır. Ama bu bir **çıkarım**, kayıtlı bir olgu değil -- hasta başka bir
hastanede tanı almış olabilir, ya da Pusula'ya geçmeden önceki dönem kapsam
dışında kalır. Yanlış `false` göndermek, doğru bilgi göndermemekten kötüdür.

**Öneri:** Bakanlığa sorulsun -- "ilk tanı" bilgisini yalnızca kendi
sistemimizdeki geçmişe göre hesaplamamız kabul edilebilir mi, yoksa alan boş mu
bırakılsın? Alan `0..1` olduğu için boş bırakmak profili bozmuyor.

### 3d. Ön tanı / kesin tanı — `verificationStatus`

**Kullanıcı düzeltmesi (2026-09-18):** "pusulada tanılar birinci ikinci değilde
ön tanı kesin tanı şeklindedir."

Doğrulandı — kaynak Pusula'nın **kendi** stored procedure'ü
`Tedavi.usp_GetEpikrizTani`:

```sql
CASE MedulaTaniTipiId WHEN 1 THEN 'Ön Tanı '
                      WHEN 2 THEN 'Kesin Tanı '
                      WHEN 3 THEN 'Ayırıcı Tani ' END
-- İngilizce sürümü: Pre-Diagnosis / Definitive Diagnosis / Differential Diagnosis
```

Bu bir **kesinlik** eksenidir ve HL7'nin `condition-ver-status` ValueSet'iyle
birebir örtüşür. `az-condition`'da `verificationStatus` **0..1**, o standart
ValueSet'e `required` bağlı — yani engel yoktu, sadece farkında değildik.

**Eski davranış hatalıydı:** `verificationStatus` her tanıda sabit `confirmed`
gönderiliyordu. Oysa son 365 günde:

| | Protokol |
|---|---:|
| Yalnızca ön tanı | **92.614** |
| Yalnızca kesin tanı | 39.169 |
| İkisi karışık | 5.574 |
| Ayırıcı tanı içeren | 73 |

Yani **98.188 protokolde (%71)** en az bir ön tanı, bakanlığa "kesinleşmiş"
olarak bildirilmişti.

**Yeni eşleştirme:**

| `MedulaTaniTipiId` | Pusula | FHIR `verificationStatus` |
|---|---|---|
| 1 | Ön Tanı | `provisional` |
| 2 | Kesin Tanı | `confirmed` |
| 3 | Ayırıcı Tanı | `differential` |
| NULL (1.327 kayıt) | — | alan hiç gönderilmez |

---

## 4. `az-observation` (Genel gözlem)

Bakanlık ateş gibi klinik ölçümlerin ve doktor notlarının `az-observation`
profiliyle gönderilmesini istiyor. Profil basit: `extension:local-system-unique-id`
(1..1), `status`, `category` (1..1), `code`, `subject`, `effective[x]` zorunlu.

### İlk tespitim yanlıştı

"Vital bulgular Pusula'da tutulmuyor" demiştim. **Kullanıcı düzeltti (2026-09-18):**
"bu alanda doktorlar epikrizde bulgular alanına giriyor."

Doğru çıktı. Vital için **ayrılmış üç ayrı yapısal yer** var ve üçü de boş:

| Yer | Durum |
|---|---|
| `Tedavi.YasamBulgusu` | 0 kayıt |
| `Tedavi.GenelMuayene` kolonları (`Ates`, `KardiyakNabiz`, `TansiyonArter`, `SPO2`, `SolunumSayisi`, `Boy`, `Kilo`) | 254.877 kaydın **hiçbirinde** dolu değil |
| `Aktarim.GenelMuayene` | aktarım tablosu |

Veri, ekrandaki formun **metne serileştirilmiş** hâlinde
`Tedavi.GenelMuayene.Bulgulari` içinde duruyor:

```
Boy:  170 cm   Kilo:  91 kg   Vücut Kitle İndeksi:  31.49   VYA:  2.02
Nabız (Dk):  77   KB-S (mmHg):  136   KB-D (mmHg):  85   SpO2:  98
Fizik Muayene Bulguları:  <serbest metin>
```

Şemaya bakıp "bu hastane vital girmiyor" demek, formun kendisine bakmamakmış.

### Kapsam (son 365 gün)

124.117 muayene kaydı / 111.550 protokol:

| Ölçüm | Kayıt | LOINC | Etiketler (TR / AZ / EN) |
|---|---:|---|---|
| Boy | 46.013 | 8302-2 | Boy / Boy / Size |
| Kilo | 43.112 | 29463-7 | Kilo / Çəki / Kg |
| Vücut Kitle İndeksi | 29.855 | 39156-5 | Vücut Kitle İndeksi / Bədən Kütlə İndeksi / Body Mass Index |
| Vücut Yüzey Alanı | 29.855 | 8277-6 | VYA / BSS |
| SpO2 | 16.891 | 2708-6 | SpO2 |
| Kan basıncı (sistolik) | 16.499 | 8480-6 | KB-S (mmHg) |
| Kan basıncı (diastolik) | 16.427 | 8462-4 | KB-D (mmHg) |
| Nabız | 16.268 | 8867-4 | Nabız (Dk) / Nəbz (Dəq) |
| Ateş | 6.713 | 8310-5 | Ateş / Hərarət / Pyrexia |

**Etiketler üç dilde.** Aynı hastanede hekimler TR/AZ/EN arayüz kullanıyor;
üçünü birden tanımak zorunlu -- `Çəki` bilmeyen bir ayrıştırıcı 928 kilo
ölçümünü sessizce atlar.

### Uygulama

- `VitalBulguParser` -- etiket + `:` + sayı kalıbı; yalnızca tam etiket eşleşmesi
  kabul ediliyor, üstüne **makul aralık** denetimi var. Serbest metindeki yanlış
  eşleşmeler ve açık veri giriş hataları böyle eleniyor.
- `VitalSignsMapper` -- `az-observation`, `category = vital-signs`, standart LOINC
  `display`, lokal ad `code.text`'te (bakanlık madde 7 ile uyumlu).
- **Kan basıncı tek kaynak:** FHIR'in yerleşik kalıbı sistolik+diastolik'i ayrı
  iki Observation olarak değil, `85354-9` panel kodu altında iki `component`
  olarak gönderir -- laboratuvarda istenen yapının (madde 5) aynısı.
- `VitalSignsSyncService` -- `ProtocolFullSyncService` zincirine eklendi, yani
  "Tümünü Gönder", "Seçilenleri Gönder" ve otomatik döngü üçü de gönderiyor.
  Protokol Detay ekranında ayrı bir "Vital Bulgular" satırı var.

**Ayrıştırıcı canlı veriye karşı doğrulandı:** 10.775 örnek kayıttan 35.667 ölçüm
çıktı. Elenen 84 ateş değerinin 82'si `0`, 2'si `366` -- yani aralık denetimi
yalnızca hatalı girişleri kesiyor.

### Doktor notları -- bilerek gönderilmiyor

`Sikayeti`, `Bulgulari` ve `Hikayesi` **zaten gönderiliyor**: epikriz
Composition'ının (`az-discharge-summary`) ayrı section'ları olarak
(LOINC 10154-3 Şikayət, 11348-0 Anamnez, 29545-1 Müayinə bulguları vb.). Aynı
metni bir de `az-observation` olarak göndermek bakanlığın kayıtlarında
**mükerrer** içerik oluştururdu.

---

## 5. Laboratuvar — alt parametreli tetkikler (ÖNCELİKLİ)

Bakanlığın en çok önem verdiği madde ve **profil bunu zaten destekliyor** --
`az-lab-result-observation`'da `Observation.component` tam olarak üst seviyeyle
aynı yapıda tanımlı (`component.code.coding` 1..*, `component.value[x]` 1..1,
`component.referenceRange`).

**Şu anki davranış:** Her alt parametre **ayrı bir Observation** olarak
gidiyor. Örnek: "İdrar Tetkiki" panelinin 12 alt parametresi 12 ayrı kaynak.

**İstenen:** Panel tek `Observation`, alt parametreler onun `component` dizisi
içinde.

**Elimizde gerekli veri var:** `GetLabResultsByProtokolIdAsync` zaten
`PanelAdi` ve panelin İcbari kodunu çözüyor (Protokol Detay'daki gruplama bunu
kullanıyor). Yani gruplama bilgisi hazır, değişen yalnızca FHIR kaynağının
kurulma biçimi.

**Etki büyük:** Observation sayısı ciddi düşecek (23 protokolde 1.149 başarılı
Observation'ın çoğu alt parametre). Bu, gönderim hacmini de azaltır.

**Dikkat edilecek nokta:** Panel başlığı satırının kendi sonuç değeri yok
(bugün bu yüzden "Atlandı" oluyordu). Yeni yapıda bu bir sorun değil -- panel
satırı `component` taşıyacağı için `az-lab-value-or-component` kuralını
sağlıyor. Yani bu değişiklik **bugün atlanan panel satırlarını da kurtarır**.

---

## 6. ImagingStudy

`ImagingStudyMapper` yazıldı ve `$validate`'ten geçti (2026-09-11). Gönderilmiyor
çünkü **Study Instance UID Pusula'da yok, PACS'ta duruyor** ve bu ağdan
erişilebilir çalışan bir DICOM sorgu ucu bulunamadı (test kayıtları:
`docs/pacs-imagingstudy-erisim-talebi.md`).

Uydurma bir UID göndermek, gerçek çalışmayla hiçbir zaman eşleşmeyecek sahte bir
kimlik yaymak olurdu; o yüzden kaynak üretilmiyor.

**Engel bizde değil:** PACS ekibinden ya çalışan bir C-FIND/QIDO-RS ucu ya da
Mobility API kimlik bilgisi gerekiyor. `DiagnosticReport.imagingStudy` referansı
da aynı anda çözülür (profilde `0..*`, opsiyonel).

---

## 7. LOINC `display` alanı — yapıldı

Bakanlık `display`'e "PDW" gibi lokal kısaltma değil, kodun standart açıklamasını
istiyor; lokal ad `code.text` içinde gitmeli.

### Kaynak sorunu ve çözümü

IG standart LOINC açıklamalarını **yayınlamıyor** -- `az-lab-test-codes-vs`
doğrudan `http://loinc.org`'u filtresiz kapsıyor, `expansion` yok. Bakanlığın
sandbox ucu da terminoloji sorgusu kabul etmiyor (**canlı denendi, 2026-09-23**):

```
GET /fhir/CodeSystem/$lookup?system=http://loinc.org&code=32207-3
  -> HTTP 400  "Unsupported resource type: CodeSystem. It is not in the allowed list."
GET /fhir/ValueSet/az-lab-test-codes-vs/$expand
  -> HTTP 404
```

Açıklamalar bu yüzden **HL7'nin resmî açık terminoloji sunucusundan**
(`tx.fhir.org`, LOINC **2.82**) çekilip projeye gömüldü -- `az-icd-10` ile aynı
kalıp: `Resources/loinc-display.tsv`, **922 kod**, 58 KB. Çalışma anında internet
gerekmiyor, dış servis düştüğünde gönderim durmuyor.

Bakanlığın verdiği örnek artık birebir üretiliyor:

```json
"code": {
  "coding": [{ "system": "http://loinc.org", "code": "32207-3",
               "display": "Platelet distribution width [Entitic volume] in Blood by Automated count" }],
  "text": "PDW"
}
```

### Kod çözümlemesi artık kontrol basamağına dayanıyor

LOINC kodunun son hanesi gövdesinden **Mod-10 (Luhn)** ile hesaplanır. Bu, kodun
gerçekten LOINC olup olmadığını tabloya bakmadan söylüyor. Ölçüldü (son 365 gün,
**1.555.441 lab istemi**):

| Durum | Kod | İstem | Pay | Gönderim |
|---|---:|---:|---:|---|
| Geçerli LOINC | 550 | 763.045 | %49,1 | `loinc.org` + kod |
| Geçerli LOINC + ayırıcılı ek (`1533-9-A`) | 51 | 111.643 | %7,2 | `loinc.org` + **taban kod** |
| Geçerli LOINC + bitişik harf (`10842-3C`) | 18 | 976 | %0,1 | `loinc.org` + taban kod |
| Bitişik rakam (`1558-62`) -- **belirsiz** | 26 | 92.816 | %6,0 | `other` (teyit bekliyor) |
| Sahte LOINC (`101-1`) | 14 | 5.825 | %0,4 | `other` |
| Tamamen yerel (`PAT15197`) | 294 | 581.136 | %37,4 | `other` |

**Sahte LOINC'lar:** `LIS.Test`'te `101-1, 101-2, 101-3, 101-4, 101-5` gibi diziler
var -- aynı gövdenin beş farklı kontrol basamağı olamaz, doğrusu `101-6`. Bunları
`loinc.org` sistemiyle göndermek, LOINC'ta olmayan bir kodu LOINC diye
etiketlemekti.

**Bitişik rakam neden dışarıda:** `1558-62` iki türlü okunabiliyor -- "LOINC
`1558-6` + yerel varyant `2`" ya da düpedüz `1558-62` adlı yerel kod. Kontrol
basamağı ikisini **ayıramaz**, çünkü `1558-6` zaten geçerli bir LOINC. Tahmin
edip yanılırsak açlık kan şekeri olmayan bir testi öyle bildirmiş oluruz.
Laboratuvar teyidi bekleniyor.

### `az-other-lab-test-codes` hatası düzeltildi

Önceki sürüm LOINC olmayan kodları `az-other-lab-test-codes` sistemine **kendi
yerel kodlarıyla** yazıyordu. O CodeSystem indirildi (`content: complete`):
**içinde tek bir kod var, `other`**. `Observation.code` ve `component.code` ikisi
de `az-lab-test-codes-vs`'e **`required`** bağlı olduğu için bu, ICD-10'daki
D38.1 reddinin aynısını üretirdi.

Artık `CodeableConcept` iki coding taşıyor -- `required` bağlama en az bir
coding'in ValueSet'te olmasını ister, onu `other` sağlıyor; yerel kod da
kaybolmuyor:

```json
"code": {
  "coding": [
    { "system": "http://fhir.az/CodeSystem/az-other-lab-test-codes",
      "code": "other", "display": "Digər laboratoriya testi" },
    { "system": "http://pusula.local/CodeSystem/lab-test",
      "code": "ART-001", "display": "pH" }
  ],
  "text": "pH"
}
```

**Bakanlıktan istenecek:** yerel test kodları için resmî bir sistem URI'si. O
gelene kadar `http://pusula.local/CodeSystem/lab-test` kullanılıyor.

### Laboratuvara iletilen liste

`lab-loinc-girilecek.xlsx` -- **121 koda** LOINC atanırsa oran **%49,1 → %90,8**
çıkıyor. Önerilen her kod `tx.fhir.org` `$lookup` ile doğrulandı ve resmî adı
Excel'de yanında yazıyor. Doğrulama dört yanlış tahmini yakaladı (örn. açlık
insülini sandığım `14371-9` meğer "idrarda maya" imiş).

## Durum (2026-09-18)

**Kodda yapılabilecek her şey bitti.** Kalan maddelerin tamamı dışarıdan bilgi
bekliyor:

| Beklenen | Kimden |
|---|---|
| `first-diagnosis` beklentisi (#3c) | Bakanlık |
| Güncellenmiş bölüm listesi (#2) | Bakanlık |
| Standart LOINC açıklama kaynağı (#7) | Bakanlık ya da LOINC sürümü |
| PACS DICOM sorgu ucu (#6) | PACS ekibi |

Gönderilen örneklerin yeni yapıyla tazelenmesi için sunucu güncellenmeli;
laboratuvar tarafında eski tekil Observation'lar silinip yeniden gönderilmeli
(sandbox olduğu için risk yok, doğrulandı).
