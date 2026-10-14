# Bakanlık geri bildirimine cevabımız — 14.10.2026

> **Durum: taslak.** Gönderilmeden önce kullanıcı onayı gerekiyor.
> Kaynak ölçümler: `docs/bakanlik-geri-bildirim-2026-10-13.md`

---

Sayın e-Health ekibi,

13.10.2026 tarihli değerlendirmeniz için teşekkür ederiz. Paylaştığınız
başlıkların tamamını hem kodumuza hem canlı verimize karşı inceledik; aşağıda
her madde için yapılan düzeltmeyi, ölçtüğümüz kapsamı ve varsa açık sorumuzu
bulacaksınız.

Verdiğiniz dokuz örnek kaydın tamamını gönderim günlüğümüzde bulduk; hepsi
3–10 Ekim arasında, güncel sürümle gönderilmiş kayıtlardı. Tespitleriniz
bugünkü davranışımızı doğru biçimde yansıtıyordu.

Ölçümlerimiz **01.10.2026'dan itibaren** gönderilen veriyi kapsıyor.

---

## 1 · Yapılan düzeltmeler

### 2 · Practitioner — isim alanlarında boşluk

Düzeltildi. Ad ve soyad alanlarına gönderim öncesi `trim` uygulanıyor.

İncelemenizi genişlettiğimizde aynı kusurun **hasta kayıtlarında da** bulunduğunu
gördük; `Patient.name` alanları da aynı temizlikten geçiyor. İkinci ad ve baba
adı alanları dahil edildi.

| Kayıt türü | Etkilenen |
|---|---:|
| Hekim | 19 (643 kayıtta) |
| Hasta | 17 (6.047 kayıtta) |

Çok parçalı adlardaki **iç boşluklar korunuyor** (örn. `ƏLİ RZA`); yalnızca
baştaki ve sondaki boşluklar kırpılıyor.

### 4 · Procedure — işlemi gerçekleştiren kişi

Düzeltildi. `performer.actor` artık işlemi uygulayan sağlık çalışanının
`Practitioner` kaydına referans veriyor.

Bu vesileyle `Practitioner` kapsamını da genişlettik: önceden yalnızca hekim
kadrosundaki personel gönderiliyordu. Artık hemşire, ebe, fizyoterapist,
anestezist, laboratuvar uzmanı gibi klinik personel de — bir işlemde fiilen
referans verildiğinde — gönderiliyor.

| | Adet |
|---|---:|
| Dönemdeki işlem | 36.335 |
| `performer` yazılan | **35.653 (%98,1)** |
| Kaynakta personel kaydı olmayan | 666 |

`performer.function` (roldeki görev) alanını **göndermiyoruz**: kaynak
sistemimizde işlemdeki rolü belirten bir alan yok, personel tipi ise rol değil
unvan bilgisi taşıyor. Varsayımla bir rol kodu üretmemeyi tercih ettik.

### 6.1 · DiagnosticReport — tetkikin gerçekleştirilme tarihi

`effectiveDateTime` alanı aslında gönderiliyordu; incelediğiniz kayıt, kaynak
sistemde çalışma tarihi boş olan kayıtlardan biriydi.

| Dönemdeki onaylı rapor | 2.608 |
|---|---:|
| Çalışma tarihi dolu — alan gidiyordu | 2.187 (%83,9) |
| Çalışma tarihi boş — alan gitmiyordu | 421 (%16,1) |

Boş kalan kayıtlar için bir **yedek tarih zinciri** kurduk. Yedeği varsayımla
değil ölçerek seçtik: çalışma tarihinin dolu olduğu 2.187 kayıtta adayların
gerçek değere yakınlığını karşılaştırdık.

| Aday alan | Aynı gün | 1 gün fark | 7 günden fazla |
|---|---:|---:|---:|
| **Kabul tarihi** | **2.162 (%98,9)** | 21 | 0 |
| İşlem tarihi | 2.083 (%95,2) | 67 | 4 |
| Onaylanma tarihi (`issued`) | 1.138 (%52,0) | 436 | 52 |

Bu ölçüm, *"`issued` ile `effectiveDateTime` aynı şey değildir"* yönündeki
uyarınızı kendi verimizle de doğruluyor: onaylanma tarihi, çalışma tarihinden
vakaların neredeyse yarısında farklı bir güne düşüyor. Bu nedenle `issued`
alanını yedek olarak **kullanmadık**.

Üç tarih de boşsa alan yine gönderilmiyor; varsayımsal bir tarih yazmaktansa
alanı boş bırakmayı doğru buluyoruz.

### 1 · Patient — adres bilgisi

Eklendi. `address` alanı artık `residence` dilimiyle (`use: home`,
`type: physical`) gönderiliyor.

| | Hasta |
|---|---:|
| Şehir (ve varsa açık adres) | **6.130 (%99,7)** |
| Adres yazılamayan | 20 |

Şehir ve rayon bilgisi kaynak sistemdeki tanım tablolarından çözülüyor ve
Azerbaycanca yazıma normalize ediliyor — kaynakta aynı şehir beş farklı
biçimde kaydedilebiliyor. Serbest metin adres alanı olduğu gibi `text`
alanında gönderiliyor.

`country` alanını yalnızca **tanıdığımız bir Azerbaycan şehri** çözüldüğünde
yazıyoruz. Dönemde kayıtlı 27 yerleşimin 11'i yurt dışı (Paris, Moskova,
Riyad, Taşkent vb.); bu kayıtlara ülke kodu yazmak yanlış olurdu.

Kayıtlı yasal adres (`registered` dilimi) için kaynağımız yok — kimlik adresi
alanları kaynak sistemde fiilen boş.

### 5.1 · Observation — birim kodlarının UCUM karşılıkları

Düzeltildi. Lokal birim ifadesi artık `code` alanına kopyalanmıyor; `unit`
alanında lokal gösterim korunuyor, `code` alanına gerçek UCUM karşılığı
yazılıyor.

Gönderdiğimiz kayıtlar üzerinden yaptığımız tarama 56 ayrı lokal birim ifadesi
buldu; 48'i eşleştirildi.

| | Değer |
|---|---:|
| UCUM kodu yazılan | **69.894 (%99,0)** |
| Karşılığı bulunamayan | 704 (%1,0) |

Uyardığınız iki örnek dahil, en yüksek hacimli düzeltmeler:

| Lokal | Adet | UCUM |
|---|---:|---|
| `K/uL` | 14.344 | `10*3/uL` |
| `fl` | 5.197 | `fL` |
| `mmHg` | 3.596 | `mm[Hg]` |
| `M/uL` | 1.783 | `10*6/uL` |
| `HPF` | 1.575 | `/[HPF]` |
| `mmol\L` | 1.080 | `mmol/L` |
| `ph unites` | 900 | `[pH]` |
| `sn` | 668 | `s` |

Ayrıca kaynak sistemimizde bir yazım hatası tespit ettik (`mmol\L`, ters bölü
ile); düzeltme bizim tarafımızda uygulandı.

Karşılığı bulunamayan ifadeler için **uydurma kod üretmedik** — ayrıntı
"Sorularımız" bölümünde.

### 5.3 · Observation — referans aralıkları

Düzeltildi. Sayısal alt ve üst sınırlar artık `referenceRange.low` ve
`referenceRange.high` alanlarında, birimleriyle birlikte gönderiliyor. Alt
parametreler için `component[].referenceRange` kullanılıyor.

| Referans aralığının biçimi | Adet | |
|---|---:|---|
| `a - b` → sınırlar yazılıyor | **68.835** | %96,2 |
| Tek taraflı (`>60` gibi) | 1.682 | %2,4 |
| Sayısal olmayan (`NEGATİF`) | 906 | %1,3 |
| Çok koşullu / belirsiz | 142 | %0,2 |

**Tek taraflı sınırlar konusundaki uyarınız uygulandı.** `>60` ifadesi
`low: 60` olarak gönderilmiyor; bu ifadeler ayrıştırılmıyor ve özgün hâlleriyle
`text` alanında korunuyor. Özgün ifade, sınırlar yazılabildiği durumlarda da
`text` alanında duruyor.

Cinsiyet veya klinik faza göre birden çok aralık içeren metinleri de bilerek
ayrıştırmıyoruz: böyle bir metinden tek bir aralık seçmek, hangi koşulun
geçerli olduğunu varsaymak anlamına gelirdi.

**Yöntem notu:** sınırları, laboratuvar tanım tablosundan değil, sonuç kaydının
kendi referans metninden çözüyoruz. Referans aralıkları yaşa ve cinsiyete göre
değiştiği için, kayıttaki metin laboratuvarın o hastaya fiilen uyguladığı
kuralın sonucudur; tanım tablosuna gitmek o kuralı bizim yeniden üretmemizi
gerektirirdi.

### 3 · Condition — `first-diagnosis`

Bu alanı 16.09 tarihli geri bildiriminizde de istemiştiniz. Kaynak sistemimizde
bu bilgiyi tutan bir alan yok. Bunun üzerine, **doğrulanabilir kısmını**
göndermeye başladık:

| Durum | Gönderilen | Gerekçe |
|---|---|---|
| Aynı hastada aynı tanı geçmişte **var** | `false` | Kayıtlı bir olgu — bu tanı ilk değil |
| Geçmişte **yok** | Alan gönderilmiyor | Hasta başka bir kurumda tanı almış olabilir |

Dönemdeki 7.445 tanının **3.545'inde (%47,6)** alan dolu gidiyor.

**Önemli:** Bu nedenle gönderimlerimizde `first-diagnosis` yalnızca `false`
değeriyle görünecektir. Bu, kurumumuzda hiç ilk tanı konulmadığı anlamına
gelmez; geçmiş kaydımız yalnızca kendi kurumumuzu kapsadığı için `true`
iddiasında bulunmuyoruz. İlgili sorumuz aşağıda.

---

## 2 · Açıklamalar

### 3 · Condition — `diagnosis-type` farkı

Sorduğunuz soru: *"Bu farkın kaynak veriden veya farklı tarihlerde yapılan
geliştirmelerden kaynaklanıp kaynaklanmadığını kontrol edebilir misiniz?"*

**Kaynak veriden kaynaklanıyor.** Verdiğiniz iki kaydın gövdesini inceledik:

| Kayıt | Tanı türü | `diagnosis-type` |
|---|---|---|
| `Condition/01a11ba1-5f86-719b-9e08-6b1430f6fe95` | Kesin tanı | `1 — Əsas diaqnoz` |
| `Condition/01a10193-3488-70fd-999d-b8279558ae3a` | **Ön tanı** | yazılmıyor |

Uyguladığımız kural şudur:

- **Kesin tanılara** `1 — Əsas diaqnoz` yazılır.
- **Ön tanılara** bu etiket yazılmaz; onlar `verificationStatus: provisional`
  ile gönderilir. Bir ön tanıya sıra bildiren bir etiket yazmak, henüz
  kesinleşmemiş bir tanı hakkında varsayımda bulunmak olurdu.
- Bir protokolde **birden fazla kesin tanı** bulunduğunda **hepsine**
  `Əsas diaqnoz` yazılır. Kaynak sistemimiz tanılar arasında bir öncelik sırası
  tutmuyor; hangisinin ana tanı olduğunu varsayımla seçmek yerine, kesinleşmiş
  tanıların tümünü bildirmeyi tercih ediyoruz. Dönemde bu durumda olan 180
  protokol bulunuyor.

**`2 — əlavə diaqnoz` ve `3 — Yanaşı xəstəliklər` kodlarını kullanmıyoruz.**
Kaynak sistemimizde bu ayrımı taşıyan bir alan bulunmuyor; inceleyebildiğimiz
aday alanlar (birincil tanı işareti, sıra numarası, ek tanı işareti) ya hiç
doldurulmuyor ya da protokol başına tek bir tanıyı işaret etmiyor.

Kurum tarafında tanı ekranına bir "ana tanı" seçimi eklenmesi için talep
oluşturduk; bu alan geldiğinde kodlamayı genişletebiliriz.

### 1 · Patient — medeni durum

`maritalStatus` alanı gönderime dahildir ve eşleştirmesi tamamdır; ancak kaynak
sistemde seyrek dolduruluyor.

| Dönemdeki hasta | 6.047 |
|---|---:|
| Medeni durumu kayıtlı | 730 (%12,1) |

Dolu olan kayıtların tamamı başarıyla eşleşiyor. Bu bir veri girişi konusudur
ve kurum içinde ele alınmaktadır.

---

## 3 · Sorularımız

### 3.1 · UCUM karşılığı bulunamayan birimler

Aşağıdaki sekiz ifadenin UCUM karşılığını bulamadık. Bu kayıtlarda `code` ve
`system` alanlarını **boş bırakıyor**, yalnızca `unit` alanında lokal gösterimi
gönderiyoruz; varsayımsal bir kod üretmemeyi tercih ettik.

| İfade | Adet | Tahminimiz |
|---|---:|---|
| `COI` | 556 | Cutoff index — siz de sormuştunuz |
| `s\CO` | 85 | Sinyal/cutoff oranı |
| `S/Co` | 25 | Aynı ölçü, farklı yazım |
| `NTU` | 21 | Nephelometric turbidity unit |
| `Index` | 9 | Belirsiz |
| `unite` | 4 | Belirsiz |
| `GPL` | 2 | IgG fosfolipid ünitesi |
| `MPL` | 2 | IgM fosfolipid ünitesi |

**Sorumuz:** Bu ifadeler için beklediğiniz UCUM kodları var mı? Yoksa `code` ve
`system` alanlarını boş bırakıp yalnızca `unit` göndermemiz uygun mudur?

### 3.2 · `valueQuantity` ile `unit` zorunluluğu

5.2 maddesinde, sayısal sonuçların `valueString` yerine `valueQuantity` olarak
gönderilmesini önerdiniz ve INR için şu örneği paylaştınız:

```json
{ "value": 0.95, "system": "http://unitsofmeasure.org", "code": "1" }
```

Bu örnekte `unit` alanı bulunmuyor. Ancak `az-lab-result-observation`
profilinde `Quantity.unit` **1..1 (zorunlu)** olarak tanımlı. 29.08.2026
tarihinde bu nedenle bir ret almıştık:

> `Instance count for 'Observation.value[x].unit' is 0`

Kaynak sistemimizde INR, eGFR gibi oransal/birimsiz testlerde birim alanı boş
geliyor. Dönemde bu durumda olan **3.528 sonuç** bulunuyor.

**Sorumuz:** Birimsiz sonuçlarda `unit` alanına ne yazmamızı beklersiniz?
Profil güncellenecek mi, yoksa bu sonuçları `valueString` olarak göndermeye
devam etmemiz mi uygundur?

### 3.3 · `first-diagnosis` için ülke çapında geçmiş

Yukarıda açıkladığımız üzere bu alanı yalnızca `false` değeriyle
gönderebiliyoruz; `true` demek için hastanın **hiçbir kurumda** daha önce bu
tanıyı almadığını bilmemiz gerekir.

**Sorumuz:** Bu amaçla sorgulayabileceğimiz ülke çapında bir tanı geçmişi
servisi mevcut mu? Mevcutsa `true` değerini de doğrulukla gönderebiliriz.
Değilse mevcut yaklaşımımızı onaylamanızı rica ederiz.

---

## 4 · Devam eden madde

### 6.2 · ImagingStudy bağlantısı

Sorduğunuz üzere mevcut altyapımızı kontrol ettik.

`ImagingStudy` kaynağını üreten eşleştirme çalışmamız hazır durumda; ancak
PACS sistemimizden **Study Instance UID** bilgisine erişim henüz
sağlanamadı. Bu UID olmadan geçerli bir `ImagingStudy` kaydı üretilemez ve
varsayımsal bir tanımlayıcı göndermek doğru olmaz.

Erişim talebimiz kurum içinde takip ediliyor. Sağlandığında `ImagingStudy`
kayıtlarını gönderip `DiagnosticReport.imagingStudy` bağlantısını kuracağız.

---

Değerlendirmeniz ve yönlendirmeniz için tekrar teşekkür ederiz.

İyi çalışmalar.
