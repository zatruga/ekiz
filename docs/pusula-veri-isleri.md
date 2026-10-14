# Pusula veri işleri — TRƏS gönderimini tıkayan kayıtlar

**Son ölçüm:** 14.10.2026 · sunucu gönderim günlüğü (13.10 07:25 kopyası) ·
pencere **01.10.2026'dan itibaren**

Bu listedeki maddelerin hiçbiri yazılım hatası değil — hepsi **Pusula'daki veri
eksikliği**. Gönderim yazılımı bunları kendiliğinden çözemez; kayıt düzeltilene
kadar o kalemler TRƏS'e gitmez.

> **Bu belge neden var:** liste daha önce yalnızca 13.10 tarihli bir rapor
> sayfasında duruyordu. Kalıcı olması gereken bir iş listesi tek bir sayfanın
> içinde kalmıştı; buraya alındı ve rakamlar yenilendi.

---

## Özet — etkiye göre sıralı

| # | Konu | Etkilenen kayıt | Kim düzeltir |
|---|---|---:|---|
| 1 | İcbari kodu eşleşmemiş | **6.196** | Hizmet tanımları |
| 2 | Epikriz metni boş | **3.990** | Hekim |
| 3 | FIN eksik ya da geçersiz | **919** | İK / hasta kayıt |
| 4 | Tetkik sonucu girilmemiş | **724** | Laboratuvar |
| 5 | LOINC / test kodu eksik | **272** | Laboratuvar tanımları |
| 6 | ICD tanı kodu geçersiz | **133** | Hekim |
| 7 | İşlem tarihi müayinə dışında | **44** | İşlem kaydı |

Toplam **12.278 kayıt** şu an veri eksikliği yüzünden bekliyor.

---

## 1 · İcbari kodu eşleşmemiş — 6.196 kayıt

**En büyük tek kalem.** Hizmetin İcbari Sigorta Fiyat Listesi karşılığı
girilmediği için işlem TRƏS'e hiç denenmiyor.

Gönderim kuralımız: bir hizmet ancak İcbari karşılığı varsa gönderilir
(kullanıcı kararı, 25.08.2026). Bu kural, yatak ücreti / CD ücreti gibi tıbbi
işlem olmayan kalemleri doğal olarak eleyecek şekilde seçilmişti — ama
eşleştirmesi **unutulmuş** gerçek tıbbi işlemler de aynı kovaya düşüyor.

**Yapılacak:** Hizmet tanımlarında İcbari eşleştirmesi yapılması. Hangi
hizmetlerin eşleşmediği *Bekleyen İşler → Takılan kalemler* ekranında görünüyor.

**Not:** Bu eşleştirmeler Pusula'da yapıldığı anda sisteme yansıyor — İcbari
kodu canlı sorgulanıyor, Excel'den okunmuyor. Ayrıca eşleştirilmeyecek kalemler
için *Hizmet Eşleştirme* ekranından "gönderilmez" işaretlenebilir; o zaman
takılanlar listesinden düşerler.

## 2 · Epikriz metni boş — 3.990 protokol

Epikriz kilitlenmiş ama metin alanı boş. Kilitli olduğu için gönderime aday
sayılıyor, metin olmadığı için gönderilemiyor.

**Yapılacak:** Hekimlerin epikriz metnini doldurması. Kilitli-boş epikriz
üretilmesini önleyecek bir kontrol Pusula tarafında düşünülebilir.

## 3 · FIN eksik ya da geçersiz — 919 kayıt

| | Adet |
|---|---:|
| Gönderilemeyen (FIN yok / biçim bozuk) | 856 |
| TRƏS'in reddettiği | 63 |

TRƏS kuralı: FIN **tam 7 ASCII harf/rakam** olmalı.

En sık iki sorun:
- **FIN hiç girilmemiş**
- **Türkçe klavyeyle yazılmış karakterler** — `İ`, `Ü`, `Ç`, `Ş`. Gözle doğru
  görünür ama TRƏS reddeder; `BAKİ` ile `BAKI` farklı karakterlerdir.

**Yapılacak:** Hem doktor (İK) hem hasta kayıtlarında FIN düzeltmesi. Türkçe
karakterli FIN'leri otomatik çevirmiyoruz — bir kimlik numarasını tahmin etmek,
hastayı TRƏS'te başka biri olarak kaydetme riski taşır.

## 4 · Tetkik sonucu girilmemiş — 724 kayıt

Laboratuvar isteminin sonuç değeri boş. Sonuç girildiği anda kendiliğinden
gönderilir — bekleyen işler taraması bunu 1–3 dakika içinde yakalıyor.

**Yapılacak:** Sonucu çıkmayan/girilmeyen tetkiklerin gözden geçirilmesi.
Uzun süredir açık kalanlar muhtemelen iptal edilmesi gereken istemler.

## 5 · LOINC / test kodu eksik — 272 kayıt

Testin LOINC karşılığı tanımlı değil; `Observation.code` yazılamadığı için
gönderilemiyor.

**Yapılacak:** Laboratuvar test tanımlarında LOINC eşleştirmesi. Bu çalışma
sunucudaki *LOINC Eşleştirme* sayfasından yürütülüyor.

## 6 · ICD tanı kodu geçersiz — 133 kayıt

TRƏS'in reddettiği tanılar. İki ayrı sebep:

- **Yıldızlı (`*`) ICD kodları** — bakanlığın kod listesinde bulunmuyor
  (bakanlığa soruldu, `docs/bakanlik-sorulari.md`)
- **ICD-10 alanına ICD-O morfoloji kodu girilmiş** — örn. `M8960/3`. Bu bir
  tümör morfoloji kodu, tanı kodu değil. Sistem bunları kırpıp göndermiyor;
  kırpılsaydı `M89` çıkardı ki bu **bambaşka bir tanı** olurdu.

**Yapılacak:** İlgili protokollerde tanı kodunun düzeltilmesi.

## 7 · İşlem tarihi müayinə dışında — 44 kayıt

İşlem, protokolün kapanış tarihinden **sonraya** yazılmış. TRƏS bunu kabul
etmiyor: *"Procedure date must not be after Encounter end"*.

**Yapılacak:** İşlem tarihlerinin kontrol edilmesi.

---

## Bakanlığın istediği ama tıkamayan veriler

Bunlar gönderimi engellemiyor; eksik oldukları için gönderdiğimiz kayıtlar
zayıf kalıyor.

| Konu | Durum | Not |
|---|---|---|
| **Medeni durum** | 6.047 hastanın 730'unda dolu (%12,1) | Kod tarafı hazır, alan dolunca gider |
| **Adres** | 713 kayıtta anlamsız içerik | Alan dolu ama adres değil — `baküsdf sefdsdf`, iki harflik parçalar. Kullanıcı kararıyla **olduğu gibi gönderiliyor** |
| **Ana tanı seçimi** | Pusula'da böyle bir alan yok | 944 protokolde hangi tanının ana olduğu belirlenemiyor. Tanı ekranına "ana tanı" seçimi eklenmesi talebi |

---

## Ölçümü tekrarlamak

Rakamlar sunucu gönderim günlüğünden geliyor; `Skipped` (veri eksikliği) ve
`Failed` (TRƏS reddi) kayıtları **kayıt bazında** (satır değil) sayılıyor.

Ekrandan takip için: *Genel Bakış → Eksik Veri Sebepleri / Hata Sebepleri* ve
*Bekleyen İşler → Takılan kalemler*.
