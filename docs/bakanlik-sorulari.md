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
