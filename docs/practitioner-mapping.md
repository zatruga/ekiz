# Pusula (IK.Personel) -> AZ Practitioner FHIR Mapping

Kaynak: FHIR Examples.postman_collection.json (Practitioner bolumu) + IK.Personel kolon listesi.
AZ Practitioner ornek payload'i Patient'a gore cok daha sade: identifier, active, name, qualification.

## 1. Dogrudan / net eslemeler

| Pusula kolonu (IK.Personel) | AZ Practitioner alani | Kardinalite | Not |
|---|---|---|---|
| `Id`                | (referans/izleme icin) | -- | Practitioner'in kendi extension:local-system-unique-id'si var mi ornekte gorulmedi, $validate ile teyit edilmeli |
| `Adi`               | `name.given[]`         | 1..1 (ornekte var) | |
| `Soyadi`            | `name.family`          | 1..1 (ornekte var) | |
| `CikisTarihi` (dolu mu) | `active`            | 0..1 | CikisTarihi NULL ise aktif personel, doluysa pasif |

## 2. Kimlik (identifier) -- KARAR VERILDI (Patient ile ayni karar)

`IK.Personel.TCKimlikNo` degeri dogrudan FIN olarak gonderilecek, tip ayrimi yapilmayacak
(bkz. `patient-mapping.md` bolum 2). Format (`^[A-Z0-9]{7}$`) uymayan kayitlar `$validate`
tarafindan reddedilecek, bu kayitlar loglanip ayri bir listede izlenecek.

## 3. Uzmanlik/Brans (qualification) -- COZULDU: ZORUNLU DEGIL, v1'de ERTELENDI

DB'de dogrudan sorgulandi: `IK.Personel` ile `Ortak.Brans`/`Ortak.Bolum` arasinda ne
FK constraint'i ne de ayri bir junction (iliski) tablosu var -- bu baglanti Pusula'da
DB seviyesinde degil, uygulama/is mantigi seviyesinde tutuluyor (nereye yazildigi belirsiz).

Sandbox'ta canli test edildi: `qualification` alani OLMADAN gonderilen bir Practitioner
`$validate`'i basariyla gecti (HTTP 200). Yani bu alan zorunlu degil (muhtemelen 0..*).

**Karar:** v1 kapsaminda Practitioner icin `qualification` GONDERILMEYECEK. Ileride
gerekirse, otomatik cikarim yerine hastane tarafindan elle bakimi yapilan kucuk bir
"doktor -> AZ medical-specialty kodu" listesi olusturulabilir (305 satirlik dagitik
`Ortak.Brans` verisinden otomatik/guvenilir cikarim yapmak riskli).

## 4. Diger acik maddeler

- `PersonelTipiId` -- hangi personel tipinin "Practitioner" olarak gonderilecegini filtrelemek
  icin kullanilabilir (sadece doktorlar mi, hemsireler de dahil mi?). Deger listesi lazim.
- Practitioner'in kendi extension:local-system-unique-id kullanip kullanmadigi ornek JSON'da
  net degil -- $validate ile teyit edilmeli.
