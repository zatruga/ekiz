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
