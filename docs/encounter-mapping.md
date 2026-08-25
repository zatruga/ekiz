# Pusula (hasta.protokol) -> AZ Encounter FHIR Mapping

Kaynak: FHIR Examples.postman_collection.json (Encounter bolumu) + hasta.protokol kolon listesi
(118 kolon -- buyuk cogunlugu Turkiye SGK/Medula/fatura alanlari, AZ FHIR ile ilgisiz, bu
dokumanda sadece ilgili kolonlar listelenmistir) + sandbox'ta canli `$validate` testleri
(2026-08-19) + DB'den dogrudan sorgulanan gercek veri dagilimlari.

## 0. SANDBOX'TA DOGRULANMIS BULGULAR

1. **`Encounter.type`, `Encounter.serviceType`, `Encounter.serviceProvider` UCU DE ZORUNLU
   (1..1).** Bunlar olmadan `$validate` reddediyor ("Instance count ... is 0, which is not
   within the specified cardinality of 1..1"). Practitioner'daki `qualification` gibi
   ertelenemez -- bu ucu de cozulmeden Encounter gonderilemez.
2. `qualification` (Practitioner) farkli olarak buradaki alanlar opsiyonel DEGIL, dikkat.

## 1. Dogrudan / net eslemeler

| Pusula kolonu (hasta.protokol) | AZ Encounter alani | Kardinalite | Not |
|---|---|---|---|
| `Id`             | `extension:local-system-unique-id` | muhtemelen 1..1 (Patient'taki gibi) |  |
| `HastaId`        | `subject` (Patient referansi)       | 1..1 | Patient'in AZ tarafindaki gercek FHIR id'sine referans -- bkz. asagida "id stratejisi" |
| `DoktorId`       | `participant.individual` (Practitioner referansi) | 0..1 | Practitioner'in AZ tarafindaki gercek FHIR id'sine referans |
| `AcilisTarihi`   | `period.start`  | 1..1 | smalldatetime -> ISO datetime+timezone (+04:00) |
| `KapanisTarihi`  | `period.end`    | 0..1 | kapanmamis protokollerde NULL olabilir |

## 2. `type` (encounter-type) -- KESINLESTI

AZ CodeSystem `http://fhir.az/CodeSystem/encounter-type` (6 kod): 1=Stasionar(Inpatient),
2=Ambulator(Outpatient), 3=Evdə(At home), 4=Sanatoriya, 5=Ambulator+Stasionar, 6=konsultativ.

DB'den son 90 gunun gercek dagilimi sorgulandi -- `GelisTipiId` sadece 3 deger aliyor:

| Pusula GelisTipiId | Adet (90 gun) | Anlam (tahmin) | AZ encounter-type |
|---|---|---|---|
| A | 51996 | Ayaktan/Ambulatuvar | 2 (Ambulator) |
| Y | 2723 | Yatan hasta | 1 (Stasionar) |
| G | 4962 | Gunubirlik (ayni gun yatis+taburcu) | **KARAR (2026-08-19): 2 (Ambulator)** -- A ile ayni muamele gorecek |

`YatisTipiId` ve `ProtokolTipiId` bu mapping icin GEREKLI DEGIL -- `GelisTipiId` tek basina
yeterli sinyal veriyor (YatisTipiId zaten sadece yatan hastalarda doluyor, SGK oda sinifi
gibi gorunuyor, AZ tarafinda karsiligi yok).

## 3. `serviceType` (hospital-departments) -- KARAR DEGISTI: ISIM-BAZLI OTOMATIK ESLESTIRME TERK EDILDI

Onceki yaklasim (isim bazli otomatik eslesme + "Digər"(999) fallback) kullanici tarafindan
REDDEDILDI (2026-08-20) -- belirsiz/yanlis eslesme riski tasidigi icin ("Dermatologiya" gibi
tek kelimelik isimlerin AZ listesindeki baska bir seyle yanlislikla eslesmesi gibi).

**Yeni yaklasim: BIREBIR, elle eslestirme.** `BolumMappingStore` (SQLite, `BolumMapping`
tablosu) `Pusula BolumId -> AZ kod` esini tutuyor. Web'de yeni bir sayfa var:
**Bölüm Eşleştirme** (`/BolumEslestirme`, sidebar > Sistem) -- son 365 gunde gercekten
kullanilan 65 bolumu (`hasta.protokol.BolumId` uzerinden, `PusulaRepository.
GetUsedDepartmentsAsync`), en cok kullanilana gore sirali gosterir, her satirda bir AZ kod
dropdown'u; "Kaydet" ile hepsi birden kaydedilir. `docs/department-mapping.md`'de iki listenin
(Pusula'da kullanilan 65 bolum + AZ'nin 51 kodu) referans dokumantasyonu var.

`EncounterMapper.Map` artik `IReadOnlyDictionary<int,string?> bolumMap` parametresi aliyor
(`EncounterSyncService` her cagride `BolumMappingStore.GetAllAsync()` ile besliyor).
Protokolun `BolumId`'si bu haritada YOKSA veya AZ kodu bos ise, Encounter tahmini bir koda
(orn. "Digər"/999) DUSURULMEZ -- SKIPPED olarak, hangi bolumun eslestirilmesi gerektigini
soyleyen net bir mesajla loglanir: *"Bölüm 'X' (Id=Y) için AZ eşleştirmesi henüz yapılmadı"*.
Canli test edildi (2026-08-20): Qastroenterologiya(30)->6 ve Onkologiya(761)->17 eslestirildi,
$validate basarili; eslestirilmemis bir bolum (Təcili Tibbi yardım/19) dogru sekilde SKIPPED
oldu, 999'a dusmedi.

## 4. `serviceProvider` (Organization) -- COZULDU (2026-08-20, bakanlik cevabi)

Sandbox'ta canli denendi:
- `GET /fhir/Organization` (parametresiz) -> reddedildi: "The 'local-system-unique-id'
  search parameter is required. No other search parameters are supported." Yani Organization
  listelenemiyor/aranamiyor, sadece bilinen bir `local-system-unique-id` ile bulunabiliyor.
- `GET /fhir/Organization/{healthcareProviderId}` (ProviderID'yi dogrudan resource id
  sanarak) -> 404 Not Found. Yani ProviderID, Organization'in FHIR id'si degil.

Ek denemeler (2026-08-19):
- `GET /fhir/Organization?local-system-unique-id={ProviderID}` -> `total: 0`, ProviderID
  bir Organization identifier'i degil.
- `POST /fhir/Organization/$validate` (bos/minimal) -> `identifier:facility-id` (1..1) ve
  `type` (1..1) zorunlu cikti. Yani Organization'in kendi profili de bir "tesis kodu"
  identifier'i bekliyor.
- `hasta.protokol.SaglikTesisKodu` -> DB'de TUM kayitlarda BOS. Pusula tarafinda hicbir
  yerde kullanilmiyor, buradan da cikarilamaz.

**Bakanlik cevabi (2026-08-20):** "Liv Bona Dea" hastanesinin bakanlikta tanimli kurum Id'si
**`5204`**. Organization'i biz olusturmuyoruz, zaten var -- dogrudan referans veriliyor:

```json
"serviceProvider": {
  "reference": "Organization/5204",
  "display": "Liv Bona Dea"
}
```

Sabit deger, hasta/protokol bazinda degismiyor -- `EncounterMapper` icinde sabit olarak
kullanilacak (config'e tasima ihtiyaci simdilik yok, tek hastane).

Ayni yazismada ikinci soru da soruldu: gonderilen test kayitlarinin dogrulugu nasil kontrol
edilecek? Cevap: her POST sonrasi donen id ile `GET {ResourceType}/{id}` yapilip gonderilen
veri sunucudan geri okunabilir; ayrica `POST {ResourceType}/$validate` ile onceden kontrol de
mumkun (zaten `EHealthClient.ValidateAsync` bunu kullaniyor). Ek bir kod degisikligi
gerektirmiyor -- dashboard'daki Detay sayfasi zaten donen AZ kaynak Id'sini ve ham response'u
gosteriyor.

## 5. Sonraki adimlar

1. ~~Organization/serviceProvider kimligi~~ -- KARARLANDI: `Organization/5204` (bolum 4).
2. ~~"G" (Gunubirlik) GelisTipiId'si icin AZ encounter-type karari~~ -- KARARLANDI: 2 (Ambulator).
3. ~~`Ortak.Bolum` -> `hospital-departments` eslestirme kodu~~ -- YAZILDI
   (`EncounterMapper.ResolveDepartment`): tam isim eslesmesi -> yoksa ilk-kelime bazli tekil
   eslesme -> yoksa "Digər"(999) fallback + not (`MappingResult.Success.Note`, SyncLog'a
   Message olarak yansir). Canli $validate ile iki senaryo da dogrulandi (2026-08-20):
   tam eslesme (Qastroenterologiya->6) ve kismi eslesme (Təcili Tibbi yardım->Təcili
   yardım/100, notla birlikte).
4. ~~Kapanmamis protokoller ne zaman gonderilsin~~ -- KARARLANDI (2026-08-20): varsayilan
   olarak protokol kapanana kadar beklenir. Ama DB'de dogrulandi (son 90 gun): Y ve G neredeyse
   hep kapaniyor (%95-97), ama **A (Ayaktan) protokollerin %21'i acik kaliyor**, ve bunlarin
   onemli bir kismi (90+ gun acik: ~2245 kayit) muhtemelen HIC kapanmayacak -- bu yuzden
   tek basina "kapanana kadar bekle" kurali Ayaktan hastalarin onemli bir kismini surekli
   gondermeme riski tasiyordu. Cozum: **Ayarlar** sayfasindan yapilandirilabilir bir gun
   esigi eklendi (`SettingsStore`, key: `OpenProtokolSendAfterDays`, varsayilan 7) --
   protokol kapanmasa bile bu kadar gun gecince gonderime uygun sayilir (`EncounterMapper.
   IsEligibleForSend`). Tur bazinda (A/Y/G) ayri esik yok, kullanicinin tercihiyle tek ve
   genel bir esik. Web'de Protokol Listesi + Detay'da "Bekliyor" / "Gonderime hazir"
   olarak gorunur goruntuleniyor (bkz. Index.cshtml, Protokol.cshtml), ayrica "Protokol
   durumu: Acik/Kapali" filtresiyle takip edilebiliyor. NOT: bu esik su an sadece web'de
   GORUNURLUK/manuel-gonderim rehberligi icin kullaniliyor -- otomatik/surekli bir donguyu
   henuz beslemiyor (bkz. madde 6).
5. ~~`HastaId`/`DoktorId` -> AZ FHIR id cozumlemesi~~ -- YAZILDI ve dogrulandi
   (`EncounterSyncService`): Encounter.subject icin Patient'in AZ tarafinda zaten senkron
   edilmis olmasi sart (arama: `local-system-unique-id` = hastaId). KARAR/GUNCELLEME
   (2026-08-20, kullanici istegi): artik bulunamazsa Encounter otomatik SKIPPED olmuyor --
   `liveMode=true` ise `EncounterSyncService` Patient'i KENDISI canli olarak once gonderir
   (`PatientSyncService.SyncOneAsync(liveMode:true)`), basarili olursa Encounter'a devam
   eder. Kullanicinin "once hasta gitti mi" diye ayrica kontrol etmesine gerek yok. Hasta
   gonderimi de basarisiz olursa (Skipped/Failed) Encounter SKIPPED loglanir, sebebi
   (hasta neden gonderilemedi) mesaja eklenir. `liveMode=false` (Worker'in validate-only
   test modu) durumunda otomatik canli gonderim YAPILMAZ, eski davranis (bulunamazsa
   SKIPPED) gecerli -- sadece dogrulama sirasinda kalici veri olusturulmamali. Doktor
   (participant, 0..1) ayni sekilde aranir ama otomatik gonderilmez (Practitioner sync
   henuz yok), bulunamazsa alan sadece atlanir (Encounter yine gonderilir).
6. **KARAR (2026-08-20):** Dashboard'daki "Gönder"/"Tekrar Gönder" butonlari (Patient ve
   Encounter, hem Protokol Detay hem Kayit Detayi sayfalarinda) artik varsayilan olarak
   CANLI gonderim yapiyor (`liveMode:true`) -- eskiden hep validate-only calisiyordu. Ayrica
   Protokol Listesi'ne toplu secim eklendi (checkbox + "Tümünü Seç" / "Hatalı Olanları Seç"
   / "Seçilenleri Gönder") -- her satir icin ayni canli EncounterSyncService.SyncOneAsync
   (hasta-cascade dahil) cagriliyor. Worker.cs'teki CLI/env-var tabanli test modu hala
   SEND_LIVE=false varsayilaniyla guvenli kaliyor, degismedi.
6. **[YENI ACIK MADDE]** Encounter icin surekli/otomatik senkron dongusu yok -- Worker.cs
   sadece manuel tek-protokol testi icin (`RESOURCE_TYPE=Encounter` + `TARGET_PROTOKOL_ID`).
   Patient'teki gibi "son N protokolu tara" dongusune baglanmasi ayri bir adim.
7. **[YENI ACIK MADDE]** Practitioner mapper/sync kodu yok -- yazilirsa `participant` alani
   dolu gonderilebilir (su an her zaman bos).
