# Bakanlık inceleme geri bildirimi — 2026-10-13

Bakanlık (e-Health) ekibi son gönderimleri **örnekleme yoluyla** inceledi, kayıtların
genel olarak başarılı aktarıldığını belirtti ve 6 başlıkta öneri paylaştı. Aşağıda her
madde **koda ve canlı Pusula verisine karşı** doğrulandı.

**Ölçüm penceresi: 01.10.2026'dan itibaren** (kullanıcı kararı — gönderim bu tarihten
başlıyor, öncesi bizim için anlamlı değil).

Bakanlığın verdiği **9 örnek kaydın hepsi** sunucu günlüğünde bulundu: 03–10 Ekim arası,
**güncel kodla** gönderilmişler. Yani hiçbiri "eski sürümden kalma" değil — tespitleri
bugünkü davranışı anlatıyor.

| # | Konu | Durum | Not |
|---|---|---|---|
| 1 | `Patient.address` | ⚠ **Karar gerekiyor** | Kaynakta adres YOK — yalnızca şehir adı, beş ayrı yazımda |
| 1b | `Patient.maritalStatus` | ✅ Kod doğru | Pusula'da %12,1 dolu — **veri tarafı** |
| 2 | Ad/soyad `trim` | ✅ **Yapıldı** (`16e8a24`) | Hastada da vardı, o da düzeltildi |
| 3a | `Condition.diagnosis-type` | ✅ Kod doğru | **Hata değil** — ön tanıya sıra kodu yazılmıyor |
| 3b | `Condition.first-diagnosis` | ⛔ **Bloke** | 16.09'da sorduk, cevap gelmedi |
| 4 | `Procedure.performer` | ✅ **Yapıldı** (`6213bba`) | Practitioner klinik personele genişletildi |
| 5.1 | Lab birim → UCUM | ⬜ **Yapılacak** | 56 ayrı birim, 43'ü düzeltilecek |
| 5.2 | Sayısal sonuç → `valueQuantity` | ⬜ **Yapılacak** | 3.528 değer — profil çelişkisi var |
| 5.3 | `referenceRange.low/high` | ⬜ **Yapılacak** | 71.565 kayıt — **en riskli madde** |
| 6.1 | `DiagnosticReport.effectiveDateTime` | ✅ **Yapıldı** (`235a85e`) | Kod zaten doğruydu, veri eksikti |
| 6.2 | `ImagingStudy` bağlantısı | ⛔ **Bloke** | PACS erişimi — mapper hazır, bağlanmamış |

---

# BÖLÜM A — YAPILANLAR

## 2. Ad ve soyad alanlarında `trim` — ✅ `16e8a24`

**Bakanlık:** `"given": ["CABİR "]` yerine `["CABİR"]` olmalı; ad/soyad alanlarına
gönderim öncesinde `trim` uygulanmalı.
(örnek: `Practitioner/01a10195-7017-752c-80c0-6c77ac93591f`)

### Ölçüm — ve ölçüm tuzağı

İlk sayım **643 doktorun hepsini "temiz"** gösterdi. Yanlıştı:

> SQL Server'da **sondaki boşluklar string karşılaştırmasında yok sayılır**
> (`'CABİR ' = 'CABİR'` → TRUE) ve `LEN()` de sondaki boşluğu saymaz. Dolayısıyla
> `Adi <> LTRIM(RTRIM(Adi))` testi sondaki boşluğu **asla** yakalayamaz.

Doğru ölçüm `DATALENGTH` ile yapıldı (bayt sayar, farkı görür):

| Kayıt türü | Toplam | Boşluklu | Oran |
|---|---:|---:|---|
| Doktor | 643 | **19** (12 ad, 4 her ikisi, 3 soyad) | %3,0 |
| Hasta | 6.047 | **17** (12 ad, 4 soyad, 1 her ikisi) | %0,3 |

**Bakanlık yalnızca doktorda gördü; hastada da vardı.**

### Nasıl yapıldı

- `src/PusulaEHealthSync/AdSoyad.cs` — `Temizle(string?)` yardımcısı. `AzPhone` /
  `AzFin` ile aynı aile ve aynı desen: ölçüm yorumda duruyor.
- Bağlandığı alanlar:
  - `PractitionerMapper` → `name.family`, `name.given`
  - `PatientMapper` → `name.family`, `name.given[0]`, ikinci ad (`Adi2`),
    **baba adı** (`fathers-name` extension)
- Boş ya da yalnızca boşluktan ibaret değer `null` döner; çağıran taraftaki
  "eksikse gönderme" denetimleri (`IsNullOrWhiteSpace`) böylece doğru çalışır ve
  TRƏS'e boş string gitmez.
- **İçteki boşluk bilerek korunuyor.** Yalnızca baş ve son kırpılıyor; çok parçalı
  adlarda (`ƏLİ RZA`) tek boşluk anlamlıdır, sıkıştırmak uydurma bir düzeltme olurdu.
  Bakanlık da yalnızca baştaki/sondaki boşluğu işaret etti.
- `.NET Trim()` SQL'den fazlasını yakalar: SQL yalnızca ASCII boşluğu (`0x20`)
  kırpar, `.NET` tüm Unicode boşluklarını (kırılmaz boşluk `U+00A0`, sekme) kırpar.
  **Yukarıdaki 19/17 bu yüzden alt sınır.**

---

## 6.1. `DiagnosticReport.effectiveDateTime` — ✅ `235a85e`

**Bakanlık:** Raporda `issued` var ama `effectiveDateTime` yok. `issued` raporun
yayımlanma zamanı, `effectiveDateTime` tetkikin ilgili olduğu klinik zaman.
(örnek: `DiagnosticReport/01a103b7-9d74-77ed-bf42-467d09d2cbd8`, TEE raporu)

### Bulgu: kod zaten doğruydu, veri eksikti

`RadiologyReportMapper` bu alanı zaten gönderiyordu — ama `CalismaTarihi` NULL ise
hiç çıkmıyordu.

| 01.10 sonrası onaylı rapor | 2.608 |
|---|---:|
| `CalismaTarihi` dolu → alan gidiyor | 2.187 (%83,9) |
| `CalismaTarihi` boş → alan gitmiyor | **421** (%16,1) |

Bakanlığın örneği bu %16'ya denk gelmiş.

### Nasıl yapıldı — yedek tarih **ölçümle** seçildi

Yedeği tahminle seçmedik. `CalismaTarihi`'nin **dolu olduğu** 2.187 kayıtta
adayların gerçeğe yakınlığı ölçüldü:

| Aday | Aynı gün | 1 gün | 2–7 gün | 7 günden fazla |
|---|---:|---:|---:|---:|
| **`KabulTarihi`** | **2.162 (%98,9)** | 21 | 4 | 0 |
| `IslemTarihi` | 2.083 (%95,2) | 67 | 33 | 4 |
| `OnaylanmaTarihi` (= `issued`) | 1.138 (%52,0) | 436 | 561 | 52 |

**`issued` yedek olamaz** — neredeyse yarısında başka bir güne düşüyor. Bakanlığın
"ikisi aynı şey değil" uyarısı böylece bizim verimizle de kanıtlanmış oldu; bu
rakam cevap yazısına konabilir.

- Zincir: `CalismaTarihi` → `KabulTarihi` → `IslemTarihi`
- Boş olan 421 kaydın **421'inde** son iki alan da dolu — zincir pratikte her zaman
  bir değer buluyor.
- Üçü de boş kalırsa alan yine hiç gönderilmiyor (`0..1`). Uydurma tarih yazmaktansa
  boş bırakmak doğru.
- İki kolon SQL'in **sonuna** eklendi, aradaki okuyucu indislerini kaydırmamak için.

---

## 4. `Procedure.performer` — ✅ `6213bba`

**Bakanlık:** Kayıt "İxtisaslı həkim müayinəsi" hizmetini içeriyor ama `performer`
alanında muayeneyi gerçekleştiren hekime referans yok.
(örnek: `Procedure/01a0fc8f-96a8-7197-931c-089e78adfd33`)

### Kaynak seçimi

Kullanıcı önce `Hizmet.YapanKaynakId`'yi işaret etti. O kolon Pusula'da
`Hasta.ProtokolIslem.HizmetKaynakId` olarak **var** — ama bu hastanede
**hiç kullanılmıyor**:

| 01.10 sonrası gönderilebilir işlem | 36.309 |
|---|---:|
| `HizmetKaynakId` dolu | **0** |
| `UygulayanPersonelId` dolu | 35.643 (%98,2) |

Kullanıcı onayı (2026-10-13): *"UygulayanPersonelId doğru data onaylıyorum."*

### Practitioner artık yalnızca doktor değil

Kullanıcı: *"bu işlemi yapan doktor olmayabilir, personelde olabilir hemşirede."*

Eski kısıt `PersonelTipiId = 1` idi ve gerekçesi kodda yazılıydı — **ama o gerekçe
Encounter'ın doktoru içindi**:

> KARAR (2026-08-20): `hasta.protokol.DoktorId` son 60 günde SADECE
> `PersonelTipiId=1` referans ediyor — bu yüzden Practitioner senkronu bu tipe
> sınırlandırıldı.

`performer` eklenince referans verilen küme genişledi, dolayısıyla kısıt da genişledi.

| 01.10 sonrası işlemi fiilen yapan | İşlem | Kişi | FIN'i uygun |
|---|---:|---:|---:|
| Doktor (tip 1) | 35.653 | 153 | 34.836 |
| Fizyoterapist (tip 30) | 16 | 1 | 1 |
| Personel kaydı yok | 666 | — | — |

Hemşirelerin 313'ü kayıtlı ve 291'inin FIN'i uygun, ama **hiçbiri işlem yapan olarak
görünmüyor**. Yine de listeye alındı — kullanıcının işaret ettiği durum yarın oluşabilir.

### Nasıl yapıldı

- `PersonelRecord.KlinikTipler` — açık liste: Doktor, Misafir Doktor, Diş Doktoru,
  Anestezist, Laboratuar, Radyoloji, Hemşire, Tıbbi Personel, Ebe, ATT, Sağlık Memuru,
  Patoloji Doktoru, Psikolog, İşyeri Hekimi, Tıbbi Laboratuar Uzmanı, Eczacı,
  Fizyoterapist, Diyetisyen.
- Dışarıda: Vezne, Medikal Muhasebe, Çağrı Merkezi, İdari Personel, Tıbbi Sekreter,
  Satınalma, Raportör, Kasa, Depocu, Görüşmeci, Arge, Mutfak, Tercüman, Aktarım.
  Bunlar `Skipped` olur ve **ekranda sebebiyle görünür**, sessizce düşmez.
- **Taşkın riski yok:** `PractitionerSyncService` id ile **talep üzerine** çalışıyor,
  toplu değil — yalnızca fiilen referans verdiğimiz kişi gönderiliyor.
- **Çözümleme servisin içinde, çağıranda değil.** `ProcedureSyncService`'i iki yer
  çağırıyor (zincir + ekrandaki "Gönder"). Çağırana bırakmak aynı mantığı iki yere
  yazmak olurdu; bu projede o desenin bedeli 2026-09-15'te zaten ödendi.
- **Önbellek şarttı.** Çözümleme her işlem için çalışıyor ve kayıtlı AZ id varsa
  **uzak `GET`** atıyor — önbelleksiz 36.335 ek istek demekti ve sistem zaten
  HTTP 429 yiyor (13.10'da bir tur bu yüzden bırakıldı). Personel başına **bir
  saatlik** `ConcurrentDictionary`; TTL var çünkü bir Practitioner TRƏS tarafında
  silinebilir ve o zaman referans veren Procedure "HTTP 409: Non-existent reference"
  ile düşer.
- `performer.function` **eklenmiyor**: Pusula'da rolü söyleyen alan yok, personel
  tipi rol değil unvandır. Uydurma rol kodu göndermektense boş (`0..1`).
- Profilde `Procedure.performer` kısıtlanmamış (IG'nin `az-procedure` sayfaları
  `performed[x]`'te bitiyor) → temel FHIR R4 geçerli.

**Açık kalan:** klinik tip listesi kullanıcı onayı bekliyor; yapı canlı `$validate`
ile görülmedi.

---

# BÖLÜM B — KOD DEĞİL, CEVAP YAZILACAKLAR

## 3a. `Condition.diagnosis-type` — hata değil

**Bakanlık:** Bazı Condition kayıtlarında var, bazılarında yok. *"Bu farkın kaynak
veriden veya farklı tarihlerde yapılan geliştirmelerden kaynaklanıp kaynaklanmadığını
kontrol edebilir misiniz?"*

**Cevap: kaynak veriden.** Verdikleri iki kaydın gövdesi sunucu günlüğünden açıldı:

| Kayıt | ICD | `verificationStatus` | `diagnosis-type` |
|---|---|---|---|
| `01a11ba1-5f86-719b-9e08-6b1430f6fe95` | `Z00.0` | `confirmed` (kesin tanı) | **1 — Əsas diaqnoz** |
| `01a10193-3488-70fd-999d-b8279558ae3a` | `C34` | `provisional` (**ön tanı**) | yok |

Kural (2026-09-18 kullanıcı kararı, bakanlığa 16.09'da yazılı açıklandı):
`1/2/3` bir **sıra** eksenidir, **kesinlik** ekseni değil (o `verificationStatus`).
Bir ön tanıya "ek tanı" demek uydurma bir sıra iddiası olurdu. Alan `0..1` olduğu
için boş bırakmak profili bozmaz.

Pusula'da sıra ekseninin doğrudan kaynağı yok — ölçüldü (365 gün, 186.038 kayıt):
`IsEkTani` %100 NULL, `SiraNo` %100 NULL, `IsBirincilTani` protokol başına tek değil
(4 tanılı protokolde dördünde de 1).

## 3b. `Condition.first-diagnosis` — soru cevapsız kaldı

Bakanlık bu alanı **tekrar istedi ama 16.09'daki sorumuzu cevaplamadı.**

Pusula'da böyle bir alan yok. `IlkTaniTarihi` kolonu var ama 365 günde 186.038 kaydın
**99'unda** dolu — kullanılmıyor.

Hesaplanabilir (aynı hastanın geçmişinde aynı ICD daha önce geçmiş mi), ama bu bir
**çıkarım**, kayıtlı bir olgu değil: hasta başka bir hastanede tanı almış olabilir.
**Yanlış `false` göndermek, hiç göndermemekten kötüdür.**

**Soru tekrarlanacak:** "ilk tanı" bilgisini yalnızca kendi sistemimizdeki geçmişe
göre hesaplamamız kabul edilebilir mi, yoksa alan boş mu bırakılsın?

## 1b. `Patient.maritalStatus` — veri tarafı

Kod doğru çalışıyor (`PatientMapper`, `MedeniHaliId` → AZ `marital-status`).
Bakanlık örneklerde görmedi çünkü Pusula'da dolu değil:

| 01.10 sonrası protokolü olan hasta | 6.047 |
|---|---:|
| `MedeniHaliId` dolu | **730 (%12,1)** |
| Karşılığı olmayan (`5 = Belirtilmemiş`) | 0 |

Dolu olanların tamamı eşleşiyor. Bu bir Pusula **veri girişi** konusu.

---

# BÖLÜM C — YAPILACAKLAR

## 1. `Patient.address` — ⚠ karar gerekiyor

**Bakanlık:** Örneklerde adres bilgisine rastlamadık; kaynak sistemde mevcutsa
gönderime eklenmesi.

**Durum:** `PatientMapper`'da hiç adres yok. Ama asıl sorun kodda değil — **kaynakta
gönderilecek bir adres yok.**

### ÖLÇÜM DÜZELTMESİ (2026-10-13)

İlk raporda "veri %99,6 mevcut" yazılmıştı. **Teknik olarak doğru, anlamca yanlış:**
alan dolu, içeriği adres değil.

| `EvAdresi` metninin şekli (6.028 dolu kayıt) | |
|---|---:|
| Ortalama uzunluk | **12 karakter** |
| 10 karakterden kısa | **4.735 (%78,5)** |
| En kısa | 2 karakter |
| Ayrı değer sayısı | 1.274 |
| Rakam içeren | 707 |

En sık geçen değerler — hepsi aynı şehrin farklı yazımı:

| Değer | Hasta |
|---|---:|
| `BAKI` | 3.292 |
| `baki` | 903 |
| `BAKU` | 264 |
| `BAKU AZERBAYCAN` | 48 |
| `Bak;` | 22 |

**4.529 kayıt (%75) "Bakı"nın bir yazımı.** Bu bir posta adresi değil, serbest metin
şehir adı.

### Diğer adres kaynakları da yok

| Alan | Dolu |
|---|---:|
| `KimlikIlId` (kimlik adresi) | 13 |
| `KimlikIlceId` | 3 |
| `KimlikKoyMahalle` | 0 |
| `MahalleIdEv` | 3 |

**İl/ilçe lookup tablosu bulunamadı.** `Sube.Ortak_Il` ve `Sube.Ortak_Ilce` tabloları
var ama `Sube.Ortak_Il` **boş** (0 satır); `IlIdEv = 6385` (5.994 hastada) hiçbir
tabloda karşılık bulmuyor. Yani `IlIdEv`/`IlceIdEv` ada çevrilemiyor.

### Profil iki dilim istiyor

`az-patient`, `address`'i **slice** ediyor:

| Dilim | `use` | `type` | Anlam |
|---|---|---|---|
| `registered` | `home` | `postal` | Kayıtlı yasal adres |
| `residence` | `home` | `physical` | Mevcut ikamet adresi |

`use`/`type` serbest değil, dilime birebir uymalı. Alt alanlarda (`city`, `text`,
`country`…) ek kısıt ya da zorunlu binding yok.

### Seçenekler — kullanıcı kararı bekliyor

**A) Hiç gönderme.** Bakanlığa "kaynak sistemde yapılandırılmış adres tutulmuyor,
yalnızca şehir adı var" denir. `address` `0..*`, profili bozmaz.

**B) Yalnızca şehir gönder.** Yazımlar normalize edilir (`BAKI`/`baki`/`BAKU`/`Bak;`
→ `Bakı`), `address:residence` dilimiyle `{ use: home, type: physical, city: <şehir>,
country: AZ }` gönderilir. Normalize edilemeyen değer **hiç gönderilmez** — `AzPhone`
ile aynı ilke.

**C) Pusula'da veri girişi düzelene kadar beklenir.**

**Zorluk:** B seçilirse düşük (bir normalizasyon sözlüğü + mapper). Asıl soru teknik
değil: **şehir adından ibaret bir adres göndermeye değer mi?**

---

## 5.1. Laboratuvar birimleri → UCUM — ⬜ sırada

**Bakanlık:** `unit` alanında lokal gösterim korunabilir, ancak `code` alanında uygun
**UCUM** karşılığı bulunmalıdır. Örnekler: `sn` → `s`; `K/uL` → `10*3/uL`
(UCUM'da büyük `K` **kelvin** demektir); `COI` karşılığının kontrol edilmesi.

**Durum:** `LabResultObservationMapper.ApplyValue` lokal birimi **doğrudan `code`
alanına kopyalıyor** — hiçbir eşleştirme yok:

```csharp
["unit"] = lab.TetkikSonucuBirimi,
["system"] = "http://unitsofmeasure.org",
["code"]  = lab.TetkikSonucuBirimi,   // <-- lokal birim UCUM diye gidiyor
```

### Ölçüm — gönderdiğimiz 17.148 Observation'dan (01.10 sonrası)

**56 ayrı birim kodu** gidiyor. 13'ü muhtemelen geçerli UCUM
(`%`, `mmol/L`, `g/dL`, `mg/dL`, `U/L`, `pg`, `fL`, `ng/mL`, `mg/L`, `pg/mL`,
`U/mL`, `IU/L`, `s`). **43'ü düzeltilmeli.** En yüksek hacimliler:

| Lokal | Adet | Olması gereken | Not |
|---|---:|---|---|
| `K/uL` | 14.344 | `10*3/uL` | Bakanlık açıkça belirtti |
| `fl` | 5.197 | `fL` | **UCUM büyük/küçük harfe duyarlı** |
| `mmHg` | 3.596 | `mm[Hg]` | |
| `M/uL` | 1.783 | `10*6/uL` | |
| `HPF` | 1.575 | `/[HPF]` | |
| `mmol\L` | 1.080 | `mmol/L` | **Pusula'da ters bölü — yazım hatası** |
| `ph unites` | 900 | `[pH]` | |
| `sn` | 668 | `s` | Bakanlık açıkça belirtti |
| `COI` | 556 | **belirsiz** | Bakanlığa sorulacak |
| `µIU/ml` | 344 | `u[IU]/mL` | |
| `ng/dl`, `µg/dl`, `ug/dL`, `ng/ml`, `IU/ml`, `nmol/l`, `pg/ml` | ~450 | büyük/küçük harf düzeltmesi | |
| `s\CO`, `S/Co`, `NTU`, `Index`, `mg/24 saat` | ~150 | **belirsiz** | Bakanlığa sorulacak |

### Nasıl yapılmalı

1. `src/PusulaEHealthSync/Mapping/AzUcum.cs` — lokal birim → UCUM sözlüğü, ölçüm
   yorumda. `AzIcd10` / `AzProcedureCodes` ile aynı desen.
2. Eşleşme **büyük/küçük harf duyarsız aranmalı** ama **çıktı UCUM'un kendi
   yazımında** olmalı (`fl` → `fL`).
3. `unit` alanı **lokal gösterimi korur** (bakanlık açıkça izin verdi), `code` UCUM
   olur.
4. **Sözlükte olmayan birim için UCUM kodu UYDURULMAYACAK.** Seçenek: `code`'u hiç
   yazmamak ve yalnızca `unit` göndermek — ama bu profil kısıtına takılabilir,
   **önce `$validate` ile denenmeli**. Alternatif: değeri `valueString`'e düşürmek.
   Bu karar ölçümle verilmeli.
5. Belirsiz 6 birim (`COI`, `S/Co`, `s\CO`, `NTU`, `Index`, `mg/24 saat`) bakanlığa
   sorulacak; cevap gelene kadar o testler mevcut davranışta kalır.
6. **`$validate` terminolojiyi denetlemiyor** (bkz. proje notu) — yeşil dönmesi
   kodun doğru olduğunu **kanıtlamaz**. Doğrulama UCUM'un resmî listesine karşı
   yapılmalı.

**Zorluk: Orta-yüksek.** Mekanik ama hacimli; yanlış UCUM, hiç UCUM'dan kötüdür.

---

## 5.2. Sayısal sonuçlar `valueQuantity` olmalı — ⬜ sırada

**Bakanlık:** INR `0,95` ve eGFR `99,35` gibi değerler `valueString` gitmiş. Sayısal
sonuçlar `valueQuantity` olmalı; JSON'da ondalık ayırıcı **nokta**, sayı **tırnaksız**.
Birimsiz oranlar için `code: "1"`.
(örnek: `Observation/01a124a6-e49a-7300-ad83-1091011e7307`)

### Ölçüm (01.10 sonrası, üst seviye + alt parametreler)

| Değer tipi | Adet |
|---|---:|
| `valueQuantity` (alt parametre) | 62.457 |
| `valueQuantity` (üst) | 8.141 |
| **sayısal ama `valueString` (alt)** | **3.507** |
| **sayısal ama `valueString` (üst)** | **21** |
| metin `valueString` (doğru) | 2.329 |

Örnekler: eGFR `17,23`, idrar dansitesi `1012`, Calprotectin `6648,9`.

### Engel — bakanlığın örneği kendi profilini geçmeyebilir

Kodda yazılı canlı hata (2026-08-29):

> AZ profilinde `valueQuantity` kullanılırsa **`unit` ZORUNLU (1..1)** — sunucu
> *"Instance count for 'Observation.value[x].unit' is 0"* diyerek reddetti.

Bakanlığın INR örneğinde **`unit` yok**:
```json
{ "value": 0.95, "system": "http://unitsofmeasure.org", "code": "1" }
```

### Nasıl yapılmalı

1. **Önce `$validate`** ile bakanlığın kendi örneği denenecek. `unit` olmadan geçerse
   kodu ona göre yazarız; geçmezse bu çelişki bakanlığa bildirilir.
2. Geçmezse: birimsiz oranlarda `unit` için ne yazılacağına karar verilmeli
   (`"1"`? testin kendi adı? boş string kabul edilmez).
3. Ondalık ayırıcı: Pusula virgül kullanıyor; `ApplyValue` zaten `Replace(',', '.')`
   yapıyor ve `CultureInfo.InvariantCulture` ile ayrıştırıyor — **bu kısım hazır**.
4. Yalnızca gerçekten sayısal olanlar dönüştürülecek; `Pozitif`/`Negatif` gibi metin
   sonuçlar `valueString` kalmalı (doğru davranış).

**Zorluk: Orta** — kod küçük, ama profil sorusu çözülmeden başlanmamalı.

---

## 5.3. `referenceRange.low` / `high` — ⬜ sırada, **en riskli**

**Bakanlık:** Referans aralıkları yalnızca `text` içinde. Sayısal alt/üst sınırlar
mevcutsa `low`/`high` birimleriyle gönderilmeli. Alt parametreler için
`component[].referenceRange`.

**Önemli uyarıları:** Tek taraflı sınırların anlamı korunmalı — eGFR için `>60`
doğrudan `low: 60` olarak **gönderilmemeli**, çünkü FHIR R4'te `low: 60` değeri de
kapsar (`≥60` demektir).

### Ölçüm (01.10 sonrası)

| | Adet |
|---|---:|
| `referenceRange` yalnız `text` (alt parametre) | 63.188 |
| `referenceRange` yalnız `text` (üst) | 8.377 |
| **`low`/`high` içeren** | **0** |

### İki yol var

**A) Kaynaktan sayısal al.** `LIS.TestNormalDeger` tablosunda `SinirAlt` ve
`SinirUst` **float** kolonları var (ayrıca `SinirAltKritik`, `SinirUstPanik`).
Ama kullandığımız view (`LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId`) bunları
**sunmuyor** — yalnızca `TetkikSonucuReferansDegeri` metnini veriyor.
→ View'in ötesine geçip `TestNormalDeger`'e JOIN gerekir. **Yaş/cinsiyet kırılımı
olup olmadığı önce ölçülmeli** — referans aralıkları genellikle yaşa/cinsiyete bağlıdır
ve yanlış satırı seçmek klinik olarak yanıltıcı olur.

**B) Metinden ayrıştır.** `"9.4 - 12.5 sn"` biçimi ayrıştırılabilir. Ama
`>60`, `<0,5`, serbest metin biçimleri **ayrıştırılmamalı**.

### Nasıl yapılmalı

1. Önce `TetkikSonucuReferansDegeri` metninin **şekil dağılımı** ölçülecek: kaçı
   `a - b`, kaçı tek taraflı (`>` / `<`), kaçı serbest metin.
   *(Bu ölçüm 90 ve 7 günlük pencerelerde zaman aşımına uğradı — view ağır.
   Protokol bazlı ya da örnekleme ile tekrar denenmeli.)*
2. `TestNormalDeger` yaş/cinsiyet kırılımı ölçülecek. Varsa A yolu, doğru satırı
   seçecek kuralı gerektirir.
3. **Yalnızca iki taraflı sayısal aralıklar `low`/`high` olur.** Tek taraflı
   olanlarda sınır **yazılmaz**, özgün ifade `text` alanında korunur — bakanlığın
   kendi talimatı bu.
4. `low`/`high` birimleri 5.1'deki UCUM eşleştirmesini kullanır → **5.1 bu işin ön
   koşulu.**
5. Alt parametreler için `component[].referenceRange`.

**Zorluk: Yüksek.** Hacim büyük, kaynak belirsiz, ve **yanlış referans aralığı
göndermek hiç göndermemekten klinik olarak daha kötüdür.**

---

## 6.2. `ImagingStudy` bağlantısı — ⛔ bloke

**Bakanlık:** Radyoloji örneklerinde `imagingStudy` referansına rastlamadık. PACS'ta
görüntüleme çalışmaları tutuluyorsa `ImagingStudy` olarak gönderilip
`DiagnosticReport.imagingStudy` ile ilişkilendirilebilir. *"Mevcut altyapınızda bu
bağlantının sağlanıp sağlanamayacağını kontrol edebilir misiniz?"*

**Durum:** `src/PusulaEHealthSync/Mapping/ImagingStudyMapper.cs` **var (154 satır)**
ama **hiçbir yere bağlanmamış** — ne sync servisi var, ne `DiagnosticReport`'tan
referans veriliyor.

**Engel:** PACS'tan Study Instance UID alınamıyor. Erişim talebi
`docs/pacs-imagingstudy-erisim-talebi.md` dosyasında; 16.09 geri bildiriminde de
"Engelli" olarak işaretlenmişti.

### Nasıl yapılmalı

1. **Önce PACS erişimi.** Study Instance UID olmadan `ImagingStudy` gönderilemez —
   mapper'daki DICOM UID biçim denetimi uydurma UID'yi zaten reddeder.
2. Erişim gelince: `ImagingStudySyncService` yazılacak, `SyncRadiologyReportsAsync`
   zincirine **DiagnosticReport'tan önce** eklenecek (referans sırası).
3. `DiagnosticReport.imagingStudy` alanı `RadiologyReportMapper`'a eklenecek.

**Zorluk: Yüksek + dış bağımlılık.** Kod işi orta; engel kurum dışı.

---

# BÖLÜM D — BAKANLIĞA SORULACAKLAR

1. **`first-diagnosis`** — 16.09'daki sorumuz cevapsız. Kendi geçmişimizden çıkarım
   kabul edilebilir mi, yoksa alan boş mu bırakılsın?
2. **Belirsiz birimler** — `COI`, `S/Co`, `s\CO`, `NTU`, `Index`, `mg/24 saat` için
   UCUM karşılığı nedir? (`COI`'yi kendileri de sormuştu.)
3. **`valueQuantity` + `unit` çelişkisi** — profil `unit`'i 1..1 zorunlu tutuyor
   (29.08'de canlı hata aldık), ama INR örneğinde `unit` yok. Hangisi geçerli?
4. **`diagnosis-type`** — ön tanıya sıra kodu yazmama kararımız (kanıtıyla) teyit
   edilsin.
5. **`issued` ≠ `effectiveDateTime`** — kendi verimizdeki doğrulama paylaşılabilir:
   `OnaylanmaTarihi` yalnızca %52 aynı güne düşüyor.

---

# EK — ÖLÇÜM YÖNTEMİ VE TUZAKLAR

- **Pusula salt okunurdur.** Tüm ölçümler `PusulaRaporlar/sorgu.cmd` ile SELECT.
  SQL dosyaları: `PusulaRaporlar/raporlar/bakanlik-2026-10-13-*.sql`
- **Lab ölçümleri gönderdiğimiz kayıtlardan yapıldı**, Pusula'dan değil. Sunucu
  günlüğündeki `RequestJson` gövdeleri okundu (01.10 sonrası 17.148 Observation).
  Bu hem **çok daha hızlı** (Pusula view'ı 7 günlük pencerede bile zaman aşımına
  uğradı) hem de **doğru soruyu** cevaplıyor: Pusula'da ne var değil, **TRƏS'e ne
  gitti**.
- **Alt parametreler unutulmamalı.** İlk lab ölçümü yalnızca üst seviyeye baktı ve
  kayıtların %49,5'inde "değer yok" gördü — değerler `component[]` içindeydi.
  Birim sayısı 39 yerine **56**, `valueString` sorunu 21 yerine **3.528** çıktı.
- **SQL Server sondaki boşluğu yok sayar.** `'A ' = 'A'` TRUE döner, `LEN()` saymaz.
  Boşluk aranırken **`DATALENGTH`** kullanılmalı (bkz. `AdSoyad.cs`).
- **`tinyint` taşması:** `ISNULL(PersonelTipiId, -1)` hata verir, önce `CAST(... AS int)`.
- **GROUP BY içinde alt sorgu** SQL Server'da reddedilir → CTE'ye alınmalı.
- **`$validate` terminolojiyi denetlemez** — uydurma kodlar HTTP 200 döner. Yeşil
  `$validate` bir eşleştirmenin doğru olduğunu asla kanıtlamaz.
