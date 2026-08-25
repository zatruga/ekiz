# Pusula (hasta.hasta) -> AZ Patient FHIR Mapping

## 0. SANDBOX'TA DOGRULANMIS BULGULAR (2026-08-19, gercek $validate testleriyle)

Test kimlik bilgileriyle (Provider: Liv Bona Dea) sandbox'a gercek istekler atilarak
asagidakiler KANITLANMIS (varsayim degil):

1. **`id` alani $validate/create'de dahi ZORUNLU** -- sunucu client-assigned id istiyor,
   id vermeden "Resource to be added has no ID" hatasi donuyor. Yani biz Pusula `Id`
   degerinden kendi id semamizi (orn. `patient-{PusulaId}`) uretip gondermeliyiz.
2. **`az-valid-identifier` kurali** `identifier.type.coding` alanini da istiyor, sadece
   `identifier.system`+`value` yetmiyor -- `type.coding.system=http://fhir.az/CodeSystem/identity-document-type`
   ve `code` FIN(15)/MYI(13)/DYI(12)/Pasaport(3) olmali.
3. **`extension:sex` icin `K` ve `Q` GECERLI, `D` (Diger) GECERSIZ.** Hata mesaji: "Code 'D'
   does not exist in the value set 'Cins Value Set (Gender Value Set)'".
   **Is karari (2026-08-19):** Pusula'da "Diger" secenegi pratikte kullanilmiyor. Yine de
   `CinsiyetId='D'` olan bir kayitla karsilasilirsa, gondermeye CALISILMAYACAK (sunucuya
   istek atilmayacak) -- kod bu durumu proaktif tespit edip "gonderilemedi: gecersiz
   cinsiyet kodu" olarak loglayacak ve atlayacak. FIN format hatasindan farkli olarak burada
   sonucun reddedilecegi zaten kesin oldugu icin bosuna API cagrisi yapilmayacak.
4. Minimal alan seti (`id`, `meta.profile`, `identifier` [type+system+value], `name.family`,
   `name.given`, `birthDate`, `extension:local-system-unique-id`, `extension:fathersName`,
   `extension:sex`) ile `$validate` **basariyla gecti (HTTP 200)** -- telecom/address/
   maritalStatus/blood-group gibi alanlar olmadan da gecerli bir Patient olusturulabiliyor.
5. `/fhir/metadata` (CapabilityStatement) her resource'ta `local-system-unique-id` search
   parametresini destekliyor ve `updateCreate: false` -- yani onerilen akis: once
   `GET /fhir/Patient?local-system-unique-id={PusulaId}` ile ara, bulunamazsa POST (create),
   bulunursa donen id ile PUT (update). `conditionalCreate`/`conditionalUpdate` yok, bu
   mantigi bizim kodumuzun yonetmesi gerekiyor.
6. CapabilityStatement'ta ayrica `Organization` resource'u da destekleniyor
   (`az-healthcare-facility` profili). Bakanligin gonderdigi test kimlik bilgileri
   yazisinda "Bu kullanici altinda Liv Bona Dea hastanesi eklidir" denildigi icin,
   Organization'i muhtemelen biz olusturmayacagiz -- `local-system-unique-id` veya
   baska bir alanla arayip mevcut olani bulacagiz (serviceProvider referansi icin).

Kaynak: e-health.gov.az sandbox swagger + https://fhir.e-health.gov.az/ (AZ Patient profili) +
FHIR Examples.postman_collection.json (Patient bolumu) + Pusula hasta.hasta kolon listesi.

Not: Gercek hasta verisi bu dokumana yazilmamistir, sadece kolon/alan eslemesi var.

## 1. Dogrudan / net eslemeler

| Pusula kolonu (hasta.hasta) | AZ Patient alani                              | Kardinalite | Not |
|---|---|---|---|
| `Id`                        | `extension:local-system-unique-id`            | 1..1 (zorunlu) | Pusula PK -> string'e cevir |
| `Adi` (+ `Adi2` varsa)      | `name.given[]`                                | 1..1 (zorunlu) | given bir dizi, Adi2 varsa ikinci eleman |
| `Soyadi`                    | `name.family`                                 | 1..1 (zorunlu) | |
| `BabaAdi`                   | `extension:fathersName`                       | 1..1 (zorunlu) | Pusula'da zaten mevcut, dogrudan tasinir |
| `DogumTarihi`                | `birthDate`                                   | 1..1 (zorunlu) | datetime -> `YYYY-MM-DD`'ye kirp |
| `CinsiyetId`                | `extension:sex` (valueCode)                   | 1..1 (zorunlu) | Pusula D/K/Q kodu buyuk ihtimalle AZ'ye birebir tasinir, bkz. asagidaki tablo |
| `AktifHastaId`               | `active`                                      | 0..1 | boolean'a cevir |
| `KanGrubuId`                 | `extension:blood-group`                       | 0..1 | Pusula tablosu alindi, AZ CodeSystem karsiligi terminology API'den teyit edilmeli |
| `MedeniHaliId`               | `maritalStatus`                               | 0..1 | Pusula tablosu alindi, AZ CodeSystem karsiligi terminology API'den teyit edilmeli |
| `GSM`                        | `telecom` (system=phone, use=mobile)          | 0..1 | |
| `SabitTel`                   | `telecom` (system=phone, use=home)            | 0..1 | |
| `Email` / `Email2`           | `telecom` (system=email)                      | 0..1 | AZ profilinde ayri slice yok, genel telecom girisi olarak eklenir |

**Adres v1 kapsaminda YOK.** `Skrs.IlKodlari` / `Skrs.IlceKodlari` referans tablolari bos
cikti -- `IlIdEv`/`IlceIdEv` kodlarini isme cevirecek guvenilir bir kaynak yok. AZ Patient'ta
adres zorunlu olmadigi icin (0..1) ilk surumde adres alanini hic gondermiyoruz; ileride
referans veri netlesirse eklenir.

### 1a. Cinsiyet (CinsiyetId) deger karsiligi -- DUZELTILDI (2026-08-20, kritik hata)

**Onceki varsayim YANLISTI.** `Pusula.Cinsiyet` lookup tablosu `K`=Kişi, `Q`=Qadın, `D`=Diğer
gosteriyordu ve `hasta.hasta.CinsiyetId`'nin bu kodlari kullandigi varsayilmisti (asagida
eski tablo). Ama gercek veride DOGRULANDI: `hasta.hasta.CinsiyetId` HIC `Q` icermiyor --
tum tablo taraninca `K`=171663, `E`=159720, `D`=665 cikti. Yani kayitlar lookup tablosunun
AZ kodlariyla degil, **Turkce harflerle** giriliyor: `E`=Erkek, `K`=Kadın (lookup tablosu
muhtemelen SaglikNet/ENabız disa aktarim gibi baska bir amac icin var, gercek veri girisi
onu kullanmiyor).

Kullanicinin ilettigi dogru karsilik (2026-08-20):

| Pusula `hasta.hasta.CinsiyetId` (gercek, Turkce) | Anlam | AZ `extension:sex` |
|---|---|---|
| `E` | Erkek | `K` (Kişi) |
| `K` | Kadın | `Q` (Qadın) |
| `D` | Diğer | -- (AZ `gender-vs` value set'i reddediyor, atlanir -- bkz. bolum 0.3) |

**Etki:** Bu hata duzeltilmeden once TUM `E` (erkek, ~159 bin kayit) "desteklenmeyen kod"
diye ATLANIYORDU, TUM `K` (kadin) kayitlari ise YANLISLIKLA erkek (`K`/Kişi) olarak
gonderiliyordu. Sandbox'ta canli CREATE edilmis olan deneme hastasi (8311761, Pusula
CinsiyetId=`K`/Kadın) bu hatayla yanlis cinsiyetle olusturulmustu -- duzeltmeden sonra
canli Update ile onarildi. `PatientMapper.GenderMap` artik bu ceviriyi yapiyor.

### 1b. Kan grubu (KanGrubuId) -- KESINLESTI (terminology API'den cekildi)

AZ CodeSystem `http://fhir.az/CodeSystem/blood-group`: 1=O(I)RH+, 2=O(I)RH-, 3=A(II)RH+,
4=A(II)RH-, 5=B(III)RH+, 6=B(III)RH-, 7=AB(IV)RH+, 8=AB(IV)RH-.

| Pusula KanGrubu.Id | Pusula Adi | AZ blood-group code |
|---|---|---|
| 7 | 0 Rh+ POZİTİF | 1 |
| 8 | 0 Rh- NEGATİF | 2 |
| 3 | A Rh+ POZİTİF | 3 |
| 4 | A Rh- NEGATİF | 4 |
| 5 | B Rh+ POZİTİF | 5 |
| 6 | B Rh- NEGATİF | 6 |
| 1 | AB Rh+ POZİTİF | 7 |
| 2 | AB Rh- NEGATİF | 8 |
| 9, 10, 11, 16, 17, 18, 19 | ABO/Rh belirsiz, zayif D varyantlari | **KARSILIK YOK** -- alan opsiyonel (0..1) oldugu icin bu durumda extension:blood-group hic gonderilmemeli |

Not: Pusula'nin kendi `Id` numarasi AZ koduyla RASTGELE UYUSMUYOR (orn. Pusula Id=7 -> AZ
code=1), semantik (ABO+Rh) eslestirme yapildi, sirf Id'ye guvenilmemeli.

### 1c. Medeni hal (MedeniHaliId) -- KESINLESTI (terminology API'den cekildi)

AZ CodeSystem `http://fhir.az/CodeSystem/marital-status`: 1=Evli, 2=Subay, 3=Boşanmış,
4=Dul, 5=Ayrı yaşayan.

| Pusula MedeniHali.Id | Adi | AZ marital-status code |
|---|---|---|
| 1 | Evli | 1 |
| 2 | Bekar | 2 (Subay) |
| 4 | Boşanmış | 3 |
| 3 | Dul | 4 |
| 5 | Belirtilmemiş | **KARSILIK YOK** -- alan opsiyonel (0..1), bu durumda maritalStatus hic gonderilmemeli |

Not: Pusula Id 3(Dul)/4(Boşanmış) ile AZ code 3(Boşanmış)/4(Dul) TERS sirada -- Id'ye gore
degil isme gore eslestirildi, dikkatli olunmali.

## 2. Kimlik (identifier) cozumlemesi -- KARAR VERILDI

**Is karari (2026-08-19):** `KimlikTipiId`/`PasaportNo` ayrimi yapilmayacak. `TCKimlikNo`
alaninda ne varsa, dogrudan FIN olarak gonderilecek:

```
identifier: [{
  type: { coding: [{ system: "http://fhir.az/CodeSystem/identity-document-type", code: "15" }] },
  system: "http://fhir.az/sid/fin",
  value: <hasta.hasta.TCKimlikNo>
}]
```

`identifier.type.coding.code` her zaman sabit `"15"` (FIN) yazilacak, `KimlikTipiId`
degerine hic bakilmayacak.

**Bilinen ve kabul edilen sonuc:** Sandbox'ta canli test ettigimizde AZ'nin
`az-fin-format` kurali degerin tam olarak `^[A-Z0-9]{7}$` (7 karakter, buyuk harf+rakam)
olmasini zorunlu kiliyor. Daha once paylasilan ornek kayitta `TCKimlikNo` degeri
(`YVRN840620122000`, 17 karakter) bu formata uymuyordu -- boyle kayitlar sunucu
tarafindan reddedilecek. Bu artik bir on kosul degil, normal akisin bir parcasi:

- Format uyan kayitlar -> basariyla gonderilir.
- Format uymayan kayitlar -> `$validate`/POST reddeder -> hata loglanir, "gonderilemeyen
  kayitlar" listesine/kuyruguna dusurulur -> zaman icinde kayit ekibi tarafindan
  duzeltilmesi beklenir (bu entegrasyon kodunun degil, hastane surecinin sorumlulugu).
- Sistem bu kayitlari sessizce atlamamali -- her red bir log/rapor satiri olarak
  izlenebilir olmali (bkz. ChatGPT ile konusulan mimarideki "audit log" fikri, burada
  gercek bir kullanim alani buldu).

Ayni karar `IK.Personel.TCKimlikNo` (Practitioner) icin de gecerli -- bkz. `practitioner-mapping.md`.

## 3. Lookup tablosu durumu (guncel)

| Alan | Pusula tablosu | Durum |
|---|---|---|
| `CinsiyetId` | `Pusula.Cinsiyet` | Alindi (3 kayit: D/K/Q). AZ karsiligi taslak halinde, terminology API ile teyit gerekiyor |
| `KanGrubuId` | `Pusula.KanGrubu` | Alindi (15 kayit). AZ karsiligi HENUZ YOK, terminology API'den cekilecek |
| `MedeniHaliId` | `Pusula.MedeniHali` | Alindi (5 kayit). AZ karsiligi taslak halinde, terminology API ile teyit gerekiyor |
| `KimlikTipiId` | `Pusula.KimlikTipi` | Kullanilmayacak -- karar geregi `TCKimlikNo` her zaman FIN(15) olarak gonderiliyor, bkz. bolum 2 |
| `IlIdEv`/`IlceIdEv` | `Skrs.IlKodlari`/`Skrs.IlceKodlari` | Tablolar BOS -- adres v1 kapsaminda disarida birakildi |
| `UyrukId` | `Ortak.Uyruk` | Dogru tablo bu (Skrs.UlkeKodlari degil). ISO/AKBS/SKRS/Mernis kodlari iceren genis bir ulke referans tablosu, orn. Id=1 Kodu='US' Adi='A.B.D.' ISOKodu vs. |

### 1d. Uyruk / vatandaslik (UyrukId) -- Ortak.Uyruk

`Ortak.Uyruk` bir ulke/uyruk referans tablosu (ISO kodu, telefon kodu, Mernis kodu gibi
cok sayida alternatif kod kolonu var). AZ Patient'ta bu bilgi iki ayri extension'a
karsilik geliyor: `extension:nationality` (`http://fhir.az/CodeSystem/nationality`,
postman orneginde kod "2" = "Azərbaycanlı") ve `extension:citizenship`
(`http://fhir.az/CodeSystem/citizenship`, kod "10" = "Azərbaycan"). Bu kodlar ISO
formatinda degil, AZ'nin kendi numaralandirmasi -- yani `Ortak.Uyruk.ISOKodu` ile
dogrudan eslesmesi beklenmiyor, terminology API'den `nationality`/`citizenship`
CodeSystem'lerini cekip Pusula `Ortak.Uyruk.Id` (veya `Kodu`/`ISOKodu`) uzerinden
capraz tablo cikarmak gerekecek.

## 3a. Terminology API yapisi (iMed Terminology API v1.87)

Spec: `https://terminology-api.e-health.gov.az/swagger/v1.87/swagger.json`

| Endpoint | Amac |
|---|---|
| `GET /api/CodeSystem` | Tum CodeSystem'leri listeler |
| `GET /api/CodeSystem/{id}` | Tek bir CodeSystem'in tam icerigi (kod+aciklama listesi) |
| `GET /api/CodeSystem/$lookup` | Tek bir kodun anlamini sorgular |
| `GET /api/CodeSystem/$validate-code` | Bir kodun gecerli olup olmadigini dogrular |
| `GET /api/ValueSet/{id}/$expand` | Bir ValueSet'i (orn. gender-vs) genisletip tum secenekleri dondurur |
| `GET /api/ConceptMap/{id}` | Iki kod sistemi arasindaki resmi eslemeyi dondurebilir (varsa Pusula<->AZ icin hazir bir mapping olabilir, kontrol edilmeli) |

**GUNCELLEME:** Test kimlik bilgileri geldi, ayni token hem `/fhir` hem `terminology-api`
icin calisiyor -- ayri kimlik gerekmiyor. `blood-group` ve `marital-status` CodeSystem'leri
cekildi ve Pusula ile kesin capraz tablo cikarildi (bkz. 1b/1c). `medical-specialty` ve
`hospital-departments` de indirildi, Pusula `Ortak.Brans`/`ortak.bolum` icerigi gelince
eslestirilecek.

## 5. Yenidoğan (az-newborn-patient) -- KESINLESTI (2026-08-25)

**Sorun:** `hasta.hasta.IsBizdeDogan=1` olan kayitlarin (bizde dogan bebekler) buyuk bir
kismi -- canli veride 4981 kayittan 2564'u (~%51) -- kendi `TCKimlikNo`'suna (FIN) henuz
sahip degil (dogumda normal). Eski mantik bu durumda kaydi "TCKimlikNo bos" diye
SESSIZCE ATLIYORDU -- yani bu hastalarin yarisi hic e-Health'e gonderilmiyordu.

**Cozum:** Resmi IG'de tam bu senaryo icin ayri bir profil var:
`http://fhir.az/StructureDefinition/az-newborn-patient` (bkz. StructureDefinition-az-newborn-patient.json,
fhir.e-health.gov.az'dan indirildi). Bu profilde `Patient.identifier` min=1 max=1 --
TEK identifier yeterli, ve `mother-fin` slice'i (`system=http://fhir.az/sid/mother-fin`,
type/coding YOK, sadece system+value) ile identifikasiya YAPILABILIYOR.

Pusula'da bu akis icin gereken veri zaten var:

| Pusula kolonu (hasta.hasta) | Kullanim |
|---|---|
| `IsBizdeDogan` (bit) | Yenidogan tiki |
| `AnneTCKimlikNo` | Anne FIN'i -- `identifier:mother-fin` |

**`PatientMapper.Map` mantigi (2026-08-25):**
- `TCKimlikNo` doluysa -> her zamanki gibi `az-patient` profili + FIN identifier.
- `TCKimlikNo` bos AMA `IsBizdeDogan=1` VE `AnneTCKimlikNo` dolu -> `az-newborn-patient`
  profili, identifier = sadece `mother-fin`.
- `TCKimlikNo` bos VE (`IsBizdeDogan=0` VEYA `AnneTCKimlikNo` de bos) -> eskisi gibi
  Skipped (identifikasiya imkani yok).

**Yenidogan yolunda az-patient'tan farklar** (StructureDefinition differential'ina gore):
- `extension:fathersName` YOK (Pusula'da baba TC'si tutulmuyor, karsiligi olan
  `extension:father-fin` de opsiyonel oldugu icin hic gonderilmiyor) -- bu yuzden baba
  adi zorunlulugu da yenidoganlarda uygulanmiyor.
- `name.given` max=1 -- ikinci ad (`Adi2`) eklenmiyor.
- Sandbox `$validate` ile canli dogrulandi (2026-08-25, Pusula Id=20240063, HTTP 200/Success).

## 4. Sonraki adimlar (oncelik sirasiyla)

1. `get_brans_bolum.sql` sonucunu alip `medical-specialty`/`hospital-departments` capraz
   tablosunu tamamlamak (Practitioner/Encounter mapping icin) -- bloklayici degil, kod
   yazimiyla paralel ilerleyebilir.
2. Patient resource builder'in gercek kodunu yazmak: Pusula sorgusu -> FHIR JSON -> arama
   (`local-system-unique-id` ile var mi kontrolu) -> POST (yoksa) / PUT (varsa) -> sonucu
   logla. Ilk hedef: sandbox'ta uctan uca calistirmak.
