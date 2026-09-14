# Bakanlığa (e-Health ekibine) sorulacak sorular

Entegrasyon sırasında karşımıza çıkan, resmi IG'de (fhir.e-health.gov.az) veya
CapabilityStatement'ta net cevabı olmayan, bakanlık/e-Health ekibine sormamız
gereken sorular. Cevap geldikçe "Durum" satırı güncellenir, ilgili karar
`docs/*-mapping.md` dosyalarına taşınır.

## Açık sorular

### 1. Yenidoğan hasta -- profil geçişi (az-newborn-patient -> az-patient)

**Soru:** Bir hasta önce `az-newborn-patient` profiliyle (kendi FIN'i yok, sadece
`mother-fin` identifier'ı ile) gönderildikten sonra, hastaneye kendi FIN'i
(TCKimlikNo) sisteme girildiğinde -- aynı `Patient` kaydını `az-patient`
profiline ve gerçek `fin` identifier'ına geçirerek PUT/update mi etmeliyiz,
yoksa bu profil değişimi sunucu tarafında farklı bir akış mı gerektiriyor
(örn. yeni bir kayıt, ya da eski kaydın `mother-fin` identifier'ının ayrıca
kaldırılması)?

**Neden çıktı:** `PatientMapper.cs`'de bu geçiş kod tarafında otomatik
destekleniyor (aynı `local-system-unique-id` ile bulup profil+identifier'ı
güncelliyor) ama IG'de bu senaryonun beklenen davranışı açıkça yazmıyor,
hiç canlı test edilmedi (2026-08-28).

**Durum:** Açık.

---

### 2. Laboratuvar hizmetleri -- hem Observation hem Procedure olarak mı gönderilmeli?

**Soru:** Bir laboratuvar testi (ör. "Tam sidik analizi") hem `az-lab-result-observation`
(sonuç değerleriyle, LOINC koduyla) hem de -- Pusula'da İcbari Sigorta Fiyat
Listesi'nde ayrı bir faturalama kalemi olarak da bulunduğu için -- `az-procedure`
olarak mı gönderilmeli, yoksa aynı klinik olayın iki kaynak tipinde tekrar
gönderilmesi (mükerrer) mi sayılır? IG'de bunu netleştiren bir kural bulunamadı.

**Neden çıktı:** Protokol 50853078 incelenirken (2026-08-31) fark edildi --
`Hasta.ProtokolIslem` tablosunda laboratuvar testlerinin (Tam sidik analizi,
Metotreksat, AST/ALT/Urea/Kreatinin paneli) kendi İcbari kodlu satırları da
var, `pi.State` alanı gevşetilince (bkz. `GetIslemlerByProtokolIdAsync`) bu
kalemler İşlem (Procedure) gönderim listesine de girebiliyordu. Bakanlıktan
kesin cevap gelene kadar GÜVENLİ TARAF seçildi: `PusulaRepository.cs`'de bu
tür kalemler (`pi.HizmetId`, `LIS.Test.HizmetId` ile eşleşiyorsa) İşlem
listesinden hariç tutuluyor -- yani şimdilik SADECE Observation olarak
gönderiliyorlar, Procedure olarak tekrar gönderilmiyorlar.

---

### 3. Ölçü birimini ve referans aralığını gönderiyoruz ama portalda görünmüyor

**Soru/bildirim:** Laboratuvar sonuçlarında ölçü birimini (`valueQuantity.unit`)
ve referans aralığını (`referenceRange[0].text`) EKSİKSİZ gönderiyoruz ve
bakanlık sunucusu bunları kaydediyor -- ama e-Health portalının "Laborator
Nəticələr" ekranındaki "Ölçü Vahidi" ve "Referans" sütunları boş ("-")
görünüyor. Bizim tarafımızda bir eksiklik yok, buna rağmen kullanıcıya
gösterilmiyor -- portalın görüntüleme tarafında bir sorun olmalı, kontrol
edilmesini rica ediyoruz.

**Kanıt (protokol 50819013, "Kalsium" testi, `Observation/01a05699-553b-768c-b4e3-3ce691b6ae0c`):**

Gönderdiğimiz veri:
```json
"valueQuantity": {"value": 8.7, "unit": "mg/dL", "system": "http://unitsofmeasure.org", "code": "mg/dL"},
"referenceRange": [{"text": "8,6 - 10,2"}]
```

Bakanlık sunucusundan CANLI GET ile geri okunan veri (2026-08-31, birebir aynı):
```json
"valueQuantity": {"code": "mg/dL", "unit": "mg/dL", "value": 8.7, "system": "http://unitsofmeasure.org"},
"referenceRange": [{"text": "8,6 - 10,2"}]
```

Yani veri kaybı YOK, sunucu tarafında doğru saklanıyor -- sorun portalın
bunu okuyup göstermemesi. (Yan soru: `referenceRange`'i serbest metin
yerine yapılandırılmış `low`/`high` olarak göndermemiz gerekiyorsa, ya da
`valueQuantity.unit` için beklenen UCUM biçimiyle ilgili bir kısıtlama
varsa -- ör. Pusula'dan gelen "µg/dl" gibi ham birim string'leri bazen
geçerli UCUM değil -- bunu da netleştirmelerini rica ediyoruz.)

**Neden çıktı:** Protokol 50819013 üzerinde kullanıcı fark etti (2026-08-31)
-- 6 laboratuvar sonucu da başarıyla gönderildi (Status=Success), portalda
sonuç değeri görünüyor ama birim/referans sütunları hep boş.

**Durum:** Açık.

**Durum:** Açık.

---

### Patoloji -- SKRS yerleşim yeri kodlarının ICD-O-3 karşılığı bizde mi kalmalı?

**Soru:** `az-pathology-finding` profilinde `component:topography` 1..1 zorunlu
ve required binding ile gerçek ICD-O-3 (XBT-O-3) topografya kodu (örn. `C50.9`)
istiyor. Pusula bu bilgiyi Türkiye SKRS'sinin **iç kodu** olarak tutuyor
(`EMR.Pathology.EPulse.YerlesimYeriCode`, örn. `1212`), ICD-O-3 olarak değil.
Bakanlığın bu iki kod kümesi arasında resmî bir eşleştirme tablosu var mı,
yoksa çeviri tamamen gönderen kurumun sorumluluğunda mı?

**Neden çıktı:** v2 (Composition + Observation zinciri) yazılırken (2026-09-08).
Pusula veritabanında çevirici iki kolon var ama ikisi de hiç doldurulmamış
(`Skrs.YerlesimYeri.TopografikKodu`, `Ortak.HizmetPatolojiOzellik.YerlesimYeriSkrsKodu`
-- ikisi de 0 satır). Bu yüzden eşleştirme canlı veriden (227 farklı terim)
elle üretildi: `docs/patoloji-topografya-icdo3-eslestirme.md`. Resmî bir tablo
varsa bizimkini onunla değiştirmek gerekir.

**Durum:** Açık.

---

### Patoloji -- "neoplazma rastlanmamıştır" raporları nasıl bildirilmeli?

**Soru:** Onaylı patoloji raporlarımızın **%75'i** (canlı veride 19.305 rapor)
"neoplazma yok" sonucu taşıyor; Pusula bunu `MorfolojiKoduCode = "0000/0"` ile
kodluyor. `0000/0` geçerli bir ICD-O-3 morfoloji kodu değil ve
`icd-o-3-morphology-vs` değer kümesi `^[8-9].*` regex'i ile onu zaten dışarıda
bırakıyor. Bu raporlar için beklenen davranış nedir: (a) `az-pathology-finding`
hiç üretilmesin (bizim şu anki tercihimiz), (b) belirli bir "bulgu yok" kodu mu
kullanılmalı, yoksa (c) başka bir profil mi?

**Neden çıktı:** v2 yazılırken (2026-09-08). Şu an (a) uygulanıyor: bu raporlar
yalnızca DiagnosticReport olarak gidiyor, `extension:composition` 0..1 olduğu
için bu profile uygun. Ama bakanlık tarafında kanser kayıt istatistiği
tutuluyorsa "negatif" sonuçları da bekliyor olabilirler.

**Durum:** Açık.

---

### Patoloji -- $validate değer kümesi üyeliğini neden denetlemiyor?

**Soru:** `component:morphology` ve `component:topography` required binding
taşımasına rağmen sunucu, değer kümesinde **olmayan** kodları kabul ediyor.
Canlı sandbox'ta denendi (2026-09-08): `C99.9` (var olmayan topografya) ve
`9999/9` (uydurma morfoloji) ikisi de HTTP 200 aldı. Terminoloji sunucusunda
`az-icd-o-3` CodeSystem'i yüklü değil mi, yoksa binding denetimi bilerek mi
kapalı?

**Neden çıktı:** v2 doğrulanırken (2026-09-08). Önemli, çünkü denetim yoksa
yanlış bir organ/tanı kodu sessizce kabul edilir -- $validate'in "geçti"
demesi eşleştirmenin doğru olduğunu GÖSTERMEZ. Bizim tarafımızda tek güvence
elle gözden geçirilmiş eşleştirme tablosu kalıyor.

**Durum:** Açık.

---

### Patoloji -- ICD-O-3 morfoloji listesi güncellenecek mi? (18 kod eşleşmiyor)

**Soru:** `az-icd-o-3` CodeSystem'inde (sürüm 0.1.1) bulunmayan ama
hastanemizde fiilen kullanılan 18 morfoloji kodu var. Bunlar uydurma
değil, WHO'nun daha yeni ICD-O-3 sürümlerinde (ör. ICD-O-3.2) tanımlı gerçek
kodlar -- AZ listesi daha eski bir alt küme görünüyor. CodeSystem güncellenecek
mi, yoksa bu tanılar için eski/daha genel bir karşılık mı kullanmalıyız?

**Etki:** 104 EPulse satırı / **60 rapor**
(gönderilebilir 6.571 raporun ~%0.9'i).

| ICD-O-3 kodu | Pusula terimi | Satır | Rapor |
|---|---|---:|---:|
| `8380/2` | Atipik hiperplazi/endometrioid intraepitelyal neoplazi | 15 | 14 |
| `8811/1` | Miksoinflamatuar fibroblastik sarkom | 12 | 1 |
| `8460/2` | Seröz 'borderline' tümör-mikropapiller alt tip | 10 | 3 |
| `9751/3` | Langerhans hücreli histiyositoz, NOS | 9 | 2 |
| `8815/1` | Soliter fibröz tümör | 9 | 3 |
| `8071/1` | Keratoakantom | 9 | 9 |
| `8509/3` | İnvaziv solid papiller karsinom | 9 | 4 |
| `8832/1` | Dermatofibrosarkoma protuberans | 6 | 3 |
| `8507/3` | İnvaziv mikropapiller karsinom | 4 | 2 |
| `8992/0` | Pulmoner hamartom | 4 | 4 |
| `8651/0` | warthin tümörü | 4 | 4 |
| `8930/3` | Yüksek dereceli endometrial stromal sarkom | 3 | 1 |
| `9260/0` | Anevrizmal kemik kisti | 3 | 3 |
| `8828/0` | Nodüler fasiitis; | 2 | 2 |
| `9750/1` | Erdheim-Chester hastalığı | 2 | 2 |
| `9718/1` | Lenfomatoid papülozis | 1 | 1 |
| `8714/3` | PECOMA, Malign perivasküler epiteloid hücreli tümör | 1 | 1 |
| `8384/1` | Adenokarsinom, endoservikal tip | 1 | 1 |

**Neden çıktı:** v2 doğrulanırken (2026-09-09), AZ CodeSystem'inin tam JSON'u
indirilip hastanede kullanılan 359 morfoloji kodunun tamamı karşılaştırıldı.
Topografya tarafında böyle bir açık YOK (227 kodun hepsi listede mevcut).

**Mevcut davranışımız (kullanıcı kararı, 2026-09-09):** bu kodlar **yine de
gönderiliyor**. Gerekçe: kodlar geçerli ICD-O-3, sunucu kabul ediyor (required
binding zaten denetlenmiyor) ve alternatif, 60 gerçek kanser bulgusunu hiç
göndermemek olurdu. Bakanlık listeyi güncellerse sorun kendiliğinden kapanır.

**Durum:** Açık.

---

### Bundle profilleri -- tek tek kaynak göndermek yeterli mi, yoksa Bundle mı beklenmeli?

**Soru:** IG'de 43 profil var ve bunların **31'i Bundle** (az-ambulatory-bundle,
az-hospital-bundle, az-cancer-registry-bundle, az-referral-bundle ...). Biz bugün
kaynakları **tek tek** gönderiyoruz (POST Patient, POST Encounter, POST Procedure ...)
ve bu çalışıyor -- kayıtlar portalda görünüyor. Ama örneğin `az-ambulatory-bundle`
`Bundle.type = transaction` ile sabitlenmiş ve içinde Composition (1..1), Patient
(1..1), Encounter (1..1), Condition (1..*), Observation (1..*) zorunlu.

Sorular:
1. Tek tek kaynak göndermek **kalıcı olarak** geçerli bir yöntem mi, yoksa Bundle
   gönderimine mi geçmemiz bekleniyor?
2. Bundle'lar belirli kayıt sistemleri (kanser kaydı, sevk, hastalık izlemi) için mi
   zorunlu, yoksa her muayene için mi?
3. `az-ambulatory-bundle` içindeki `observation:disease-course` (1..1, "xəstəliyin
   gedişi") karşılığını Pusula'da nereden üretmeliyiz? Şu an böyle bir Observation
   üretmiyoruz.

**Neden çıktı:** IG profil listesi gözden geçirilirken (2026-09-11). O güne kadar
yalnızca Core + Laboratuvar/Diaqnostika kategorilerini (12 profil) uygulamıştık;
listenin tamamı görülünce 31 Bundle profilinin varlığı fark edildi.

**Özellikle ilgili olabilecekler:** `az-cancer-registry-bundle` (patoloji modülümüz
zaten ICD-O-3 morfoloji + topografya üretiyor -- kanser kayıt verisinin ta kendisi),
`az-hospital-bundle` / `az-ambulatory-bundle` (yatan/ayaktan protokollerimiz),
`az-referral-bundle` ve `az-sick-leave-bundle` (Pusula'da karşılığı var).

**Durum:** Açık.

---

### AZ Immunization ve AZ Imaging Study -- kapsam dışı bırakmamız sorun olur mu?

**Soru:** Core profillerden **AZ Immunization** ve Laboratuvar/Diaqnostika
profillerinden **AZ Imaging Study (ImagingStudy)** henüz uygulanmadı. Bunlar
bizden bekleniyor mu?

- **Immunization:** Pusula'da aşı kaydı tutuluyor mu, tutuluyorsa hangi tabloda --
  önce bunu netleştirmemiz gerekiyor.
- **ImagingStudy:** ARAŞTIRILDI (2026-09-11) -- **Pusula verisiyle üretilemiyor.**
  Profil iki kimliği de 1..1 ZORUNLU kılıyor: Accession Number (ACSN) ve Study
  Instance UID (`urn:dicom:uid`). Canlı veride ölçüldü (RIS.TetkikIslem, State=6,
  son 90 gün, 13.945 tetkik):

  | Alan | Durum |
  |---|---|
  | `AccessionNo` | **0 / 13.945 -- kolon var, hiç doldurulmamış** |
  | `PacsId` | **0 / 13.945 -- boş** |
  | `GoruntuSayisi` | 11.039 / 13.945 (%79) -- dolu |
  | Study Instance UID | **Veritabanında böyle bir kolon yok** |

  Hastanenin PACS entegrasyonu CANLI ve yoğun (`Ortak.Hl7Mesaj`: FUJIPACSMP 768.148
  mesaj, TELETIP 298.547, VNA 6.077 -- hepsinde bugün trafik var). Ama bu mesajlar
  **sipariş ve sonuç** taşıyor (ORM^O01 / ORU^R01), görüntü meta verisi değil: son 7
  günün mesajlarında DICOM UID deseni (`1.2.`) ya da Study UID taşıyan `ZDS` segmenti
  **hiç geçmiyor**. `SaglikNet.TeletipSorgu` tablosunun `GelenJson`/`GidenJson`
  alanları da 362.316 satırda **sıfır kez dolu** -- yalnızca durum kodu tutuluyor.

  **Sonuç:** Study Instance UID'ler PACS'ta (Fuji) ve VNA'da duruyor, Pusula'ya hiç
  yazılmıyor. ImagingStudy göndermek isteniyorsa Pusula'dan okumak yetmez; PACS'a
  DOĞRUDAN bir entegrasyon (DICOMweb QIDO-RS sorgusu ya da C-FIND) gerekir. Bu,
  mevcut "Pusula'yı oku, e-Health'e yaz" mimarisinin dışında yeni bir bağlantı demek.

  **"Görüntüler mi, referans mı?" sorusu IG'den CEVAPLANDI (2026-09-11):** profil
  **yalnızca meta veri** taşıyor. `az-imaging-study`'de `endpoint` elemanı YOK,
  `Binary`/`Attachment`/`Media` YOK, WADO-RS adresi YOK. Yani görüntülerin kendisini
  göndermek için profilde bir mekanizma bulunmuyor -- kaynak bir **DICOM çalışma
  kaydı** (kimlikler + modalite + sayılar), görüntü kabı değil.

  Pratik sonucu: PACS'tan **asla görüntü çekmemiz gerekmiyor**. C-MOVE / C-GET /
  WADO-RS yetkisi istemeye gerek yok, yalnızca **C-FIND** yeterli.

  **CEVAPLANDI (kullanıcı, 2026-09-14): bakanlık ImagingStudy İSTİYOR.** Dolayısıyla
  bu iş kapsamda. Kaynak üretimi yazıldı ve canlı `$validate`'ten geçti
  (`ImagingStudyMapper`, HTTP 200); tek eksik Study Instance UID -- PACS'ta duruyor
  ve erişilebilen hiçbir DICOM sorgu ucu çalışmıyor
  (bkz. docs/pacs-imagingstudy-erisim-talebi.md). PACS erişimi bu yüzden artık
  "isteğe bağlı iyileştirme" değil, **kritik yolda**.

**Durum:** Açık.

---

*(Yeni sorular buraya eklenecek.)*

## Kapanan sorular

### Laboratuvar Observation -- procedure-code extension'ı tüm testler için zorunlu mu?

**Soru:** `az-lab-result-observation` profilinde `extension:procedure-code`
cardinality 1..1 -- LOINC kodu tek başına yeterli mi, yoksa her lab
Observation'ı İcbari Sigorta Fiyat Listesi'ndeki bir hizmete de mi bağlanmalı?

**Cevap (2026-08-29):** Zorunlu -- CANLI $validate ile doğrulandı, sunucu
`"Instance count for 'Observation.extension:procedure-code' is 0, which is
not within the specified cardinality of 1..1"` diyerek reddetti. Pusula
tarafında köprü de bulundu: COMED view'i (`LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId`)
doğrudan bir Hizmet bağlantısı vermiyor, ama `LIS.Test` (Pusula'nın kendi
laboratuvar test kataloğu, YEREL tablo) hem `LoincKodu` hem `HizmetId` taşıyor
-- kullanıcının canlı SELECT'iyle (2026-08-29, 20 satırlık örnek, 19/20
eşleşti) doğrulandı. Zincir: view.LoincKodu -> LIS.Test.LoincKodu -> HizmetId
-> Ortak.Hizmet -> (Procedure kaynağıyla AYNI) İcbari eşleştirmesi. Uygulandı:
`PusulaRepository.GetLabResultsByProtokolIdAsync` artık bu zinciri JOIN
ediyor, `LabResultObservationMapper` İcbari kodu bulunamayan (LOINC eşleşmesi
yok ya da o hizmet İcbari listede değil) test sonuçlarını Skipped bırakıyor
-- procedure-code doldurulamadan gönderilemez.
