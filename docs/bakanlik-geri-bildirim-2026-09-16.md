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
| 3b | `diagnosis-type` | ✅ **Yapıldı** (`ba23eec`) | — |
| 3c | `first-diagnosis` | **Karar gerekiyor** | Pusula'da böyle bir alan yok |
| 4 | `az-observation` (vital/not) | **Yapılamıyor — gerekçeli** | Vital tablosu boş; notlar zaten epikrizde |
| 5 | Laboratuvar `component` yapısı | ✅ **Yapıldı** (`e9f1908`) | — |
| 6 | ImagingStudy | **Engelli** | PACS'tan Study Instance UID alınamıyor |
| 7 | LOINC `display` | **Kaynak gerekiyor** | IG standart LOINC adlarını yayınlamıyor |

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

### 3b. `diagnosis-type`

Profilde `Condition.extension:diagnosis-type` **0..1**, `required` binding ile
`http://fhir.az/CodeSystem/diagnosis-type`'a bağlı. Kod listesi:

| Kod | Anlam |
|---|---|
| 1 | Əsas diaqnoz |
| 2 | əlavə diaqnoz |
| 3 | Yanaşı xəstəliklər |

**Pusula'da karşılığı var:** `Tedavi.ProtokolICD.IsBirincilTani` -- 611.518
tanının 526.578'i (%86) işaretli. `IsBirincilTani = 1` → kod **1**, değilse
kod **2**.

Kod **3** (yanaşı xəstəlik / komorbidite) için Pusula'da ayrı bir işaret yok;
`IsAnaTani` alanı var ama anlamı doğrulanmadı (100.168 evet, 84.168 NULL).
**Kod 3 gönderilmeyecek** -- anlamını doğrulamadan komorbidite demek, tanı
bilgisini yanlış etiketlemek olur.

### 3c. `first-diagnosis`

Profilde `0..1`, **boolean**, binding yok. Anlamı: "bu tanı hastaya **ilk kez**
mi konuldu".

**Pusula'da böyle bir alan YOK.** `IsBirincilTani` bu değil -- o "birincil/esas
tanı" demek, "ilk kez konulan tanı" değil. İkisi farklı kavram.

Hesaplayabiliriz: aynı hastanın geçmişinde aynı ICD kodu daha önce geçmiş mi
diye bakılır. Ama bu bir **çıkarım**, kayıtlı bir olgu değil -- hasta başka bir
hastanede tanı almış olabilir, ya da Pusula'ya geçmeden önceki dönem kapsam
dışında kalır. Yanlış `false` göndermek, doğru bilgi göndermemekten kötüdür.

**Öneri:** Bakanlığa sorulsun -- "ilk tanı" bilgisini yalnızca kendi
sistemimizdeki geçmişe göre hesaplamamız kabul edilebilir mi, yoksa alan boş mu
bırakılsın? Alan `0..1` olduğu için boş bırakmak profili bozmuyor.

---

## 4. `az-observation` (Genel gözlem)

Bakanlık ateş gibi klinik ölçümlerin ve doktor notlarının `az-observation`
profiliyle gönderilmesini istiyor. Profil basit: `extension:local-system-unique-id`
(1..1) ve `category` (1..1) zorunlu.

**Sorun kaynakta:** Pusula'da vital bulgular için ayrılmış tablo
`Tedavi.YasamBulgusu` ve içinde doğru kolonlar var (`VucutSicakligi`,
`KanBasinciSYS/DIA`, `SPO2`, `SolunumSayisi`, `Nabiz`...).

**Ama tablo tamamen boş: 0 kayıt.** Hiç kullanılmamış -- en eski/en yeni tarih
bile NULL. Yani bu hastanede vital bulgular buraya girilmiyor.

Elimizde yapılandırılmış olmayan serbest metin var:

| `Tedavi.GenelMuayene` alanı | Dolu kayıt |
|---|---:|
| `Bulgulari` | 381.343 |
| `Sikayeti` | 423.902 |
| `Hikayesi` | 305.293 |

Bunlar `az-observation` olarak **not** (valueString) şeklinde gönderilebilir, ama
"ateş = 38.2 °C" gibi **kodlu ölçüm** üretilemez -- veri o biçimde tutulmuyor.

**İkinci bulgu (2026-09-18):** Doktor notları için de yapacak bir şey yok --
`Sikayeti`, `Bulgulari` ve `Hikayesi` **zaten gönderiliyor**: epikriz
Composition'ının (`az-discharge-summary`) ayrı section'ları olarak
(`CompositionMapper`, LOINC 10154-3 Şikayət / 8648-8 Tedavi-seyir vb.). Aynı
metni bir de `az-observation` olarak göndermek, bakanlığın kayıtlarında
**mükerrer** içerik oluştururdu.

**Sonuç:** Bu madde şu an yapılamıyor ve yapılmamalı:
- **Klinik ölçümler (ateş, tansiyon, SPO2):** Pusula'da yapılandırılmış olarak
  hiç tutulmuyor -- ayrılmış tablo var ama 0 kayıt. Hastane bilişim biriminden
  bu verinin gerçekte nereye girildiği öğrenilmeli.
- **Doktor notları:** zaten epikriz içinde gidiyor, tekrarı mükerrer olur.

Bakanlığa bu iki gerekçe iletilmeli.

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

## 7. LOINC `display` alanı

Bakanlık `display`'e "PDW" gibi lokal kısaltma değil, kodun standart açıklamasını
istiyor (`"Platelet distribution width [Entitic volume] in Blood by Automated
count"`), lokal ad ise `code.text` içinde gitmeli.

**Kaynak sorunu:** `az-lab-test-codes-vs` doğrudan `http://loinc.org`'u ve
`az-other-lab-test-codes`'u kapsıyor. Yani **IG standart LOINC açıklamalarını
yayınlamıyor**; `CodeSystem-az-lab-test-codes.json` adresi de HTML dönüyor
(yayınlanmamış). Pusula'daki `LIS.Test` yalnızca lokal adı tutuyor.

**Seçenekler:**
1. LOINC resmi sürümünü indirip (ücretsiz, hesap gerektirir) kod→display
   tablosunu projeye gömmek. Kalıcı ve doğru çözüm.
2. Bakanlıktan `az-lab-test-codes` CodeSystem'ini yayınlamasını istemek.

Ne olursa olsun `code.text = lokal ad` kısmı hemen yapılabilir; eksik olan
yalnızca standart `display`.

**Ayrıca:** Pusula'daki bazı kodlar LOINC bile değil (`MP15227`, `EH-011`,
`LE-001`, `9397-1-B`, `20455-2-A`). Bunlar için `az-other-lab-test-codes`
sistemine geçilmeli; şu an hepsi `http://loinc.org` sistemiyle gönderiliyor ki
bu **yanlış** -- LOINC olmayan bir kodu LOINC sistemiyle etiketliyoruz.

---

## Durum (2026-09-18)

**Kodda yapılabilecek her şey bitti.** Kalan maddelerin tamamı dışarıdan bilgi
bekliyor:

| Beklenen | Kimden |
|---|---|
| `first-diagnosis` beklentisi (#3c) | Bakanlık |
| Güncellenmiş bölüm listesi (#2) | Bakanlık |
| Standart LOINC açıklama kaynağı (#7) | Bakanlık ya da LOINC sürümü |
| Vital bulguların gerçek kaynağı (#4) | Hastane bilişim birimi |
| PACS DICOM sorgu ucu (#6) | PACS ekibi |

Gönderilen örneklerin yeni yapıyla tazelenmesi için sunucu güncellenmeli;
laboratuvar tarafında eski tekil Observation'lar silinip yeniden gönderilmeli
(sandbox olduğu için risk yok, doğrulandı).
