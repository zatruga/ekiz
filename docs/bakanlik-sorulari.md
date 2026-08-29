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

### 2. Laboratuvar Observation -- procedure-code extension'ı tüm testler için zorunlu mu?

**Soru:** `az-lab-result-observation` profilinde `extension:procedure-code`
(Azərbaycan Prosedür Kodları Value Set'e bağlı, Procedure kaynağındaki İcbari
kodla aynı sistem) cardinality **1..1** -- yani her lab Observation'ı için
zorunlu görünüyor. Ama Procedure kaynağında (kullanıcı kararı, 2026-08-25)
sadece İcbari Sigorta Fiyat Listesi ile eşleşen hizmetleri gönderiyoruz --
laboratuvar testlerinin TAMAMI bu listede olmayabilir. Eşleşmeyen bir test
için bu zorunlu alanı nasıl dolduracağız? Ya (a) LOINC kodu tek başına
yeterli görülüp bu extension aslında opsiyonel/koşullu mu uygulanıyor, ya da
(b) İcbari dışındaki testler için de kapsayan farklı/genel bir prosedür kodu
kaynağı var mı, ya da (c) İcbari eşleşmesi olmayan lab sonuçları hiç
gönderilmemeli mi?

**Neden çıktı:** Tetkik (laboratuvar) entegrasyonuna başlarken IG'nin ham
StructureDefinition JSON'ı incelendi (2026-08-29) -- `procedure-code`
extension'ının min=1 olduğu doğrulandı, henüz hiç canlı $validate denenmedi.

**Durum:** İlk kısmı CANLI $validate ile doğrulandı (2026-08-29) -- sunucu
gerçekten reddetti: `"Instance count for 'Observation.extension:procedure-code'
is 0, which is not within the specified cardinality of 1..1"`. Yani LOINC tek
başına KESİNLİKLE yetmiyor, procedure-code zorunlu. Kalan asıl soru hâlâ açık:
İcbari eşleşmesi olmayan lab testleri için bu kodu nereden bulacağız (b/c
seçenekleri hâlâ geçerli) -- Pusula tarafında `LIS.TestIslem.ProtokolIslemId`
üzerinden bir köprü olup olmadığı araştırılıyor.

---

*(Yeni sorular buraya eklenecek.)*
