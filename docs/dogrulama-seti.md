# Uçtan Uca Doğrulama Seti — 10 Protokol

**Amaç:** Bakanlık (e-Health) ekibinin gönderimlerimizi uçtan uca kontrol edebilmesi için
seçilmiş protokol kümesi. **Tek bir protokolün her şeyi içermesi gerekmiyor** -- onunun
**toplamı**, gönderdiğimiz her kaynak tipini ve her özel durumu kapsıyor.

**Hazırlanma:** 2026-09-14 · **Tarih aralığı:** 28.04.2026 – 07.09.2026 · **Hepsi kapalı**
(gönderim ölçütlerini karşılıyor).

Sunum için biçimlendirilmiş hâli: `Masaüstü/ehealth-dogrulama-seti.html`
(tarayıcıda açılır, PDF'e basılabilir).

---

## Protokol × içerik matrisi

Bütün sayılar canlı veritabanından doğrudan sayıldı.

| Protokol | Tip | Tanı | İşlem | Lab | Lab ref. | Radyoloji | Pat. neoplazili | Pat. neoplazisiz | Epikriz |
|---|---|---:|---:|---:|---:|---:|---:|---:|:--:|
| **50772329** | Yatan | 6 | 571 | 222 | **192** | **11** | 0 | 0 | — |
| **50779242** | Yatan | 0 | 325 | 144 | 131 | 5 | 2 | 0 | — |
| **50830462** | Yatan | 2 | 214 | 75 | 59 | 4 | **2** | 0 | — |
| **50831224** | Ayaktan | **12** | 6 | 26 | 11 | 1 | 0 | 0 | ✓ |
| **50833609** | Yatan | 0 | 221 | 44 | 38 | 0 | **3** | 0 | — |
| **50841776** | Ayaktan | 8 | 17 | 56 | 39 | 3 | 0 | 0 | ✓ |
| **50845123** | Ayaktan | 8 | 11 | 51 | 34 | 3 | 0 | 0 | ✓ |
| **50847194** | Yatan | 1 | 170 | 127 | 110 | 1 | 0 | 0 | — |
| **50859592** | Günübirlik | 1 | 64 | 37 | 30 | 0 | 1 | 0 | **✓** |
| **50862376** | Günübirlik | 0 | 11 | 0 | 0 | 0 | 0 | **1** | — |

### Protokol künyeleri

> **Hasta adları bu belgeye BİLEREK yazılmadı.** Depo GitHub'a gönderiliyor; hasta adı
> kişisel sağlık verisidir ve depoya girmemeli (`.gitignore`'daki "patient references --
> never commit" kuralıyla aynı gerekçe). Adlı sürüm yalnızca yerelde:
> `Masaüstü/ehealth-dogrulama-seti.html`.

| Protokol | Bölüm | Açılış | Kapanış | Not |
|---|---|---|---|---|
| 50772329 | Uşaq Sağlamlığı və Xəstəlikləri | 28.04.2026 | 04.05.2026 | yenidoğan |
| 50779242 | Torakal Cərrahiyyə | 06.05.2026 | 13.05.2026 | |
| 50830462 | Torakal Cərrahiyyə | 23.07.2026 | 27.07.2026 | |
| 50831224 | Daxili Xəstəliklər | 24.07.2026 | 24.07.2026 | |
| 50833609 | Mamalıq-Ginekologiya | 28.07.2026 | 31.07.2026 | |
| 50841776 | Check Up | 08.08.2026 | 08.08.2026 | |
| 50845123 | Check Up | 13.08.2026 | 13.08.2026 | |
| 50847194 | Uşaq Sağlamlığı və Xəstəlikləri | 16.08.2026 | 20.08.2026 | yenidoğan |
| 50859592 | Uşaq Cərrahiyyə | 03.09.2026 | 03.09.2026 | |
| 50862376 | Qastroenterologiya | 07.09.2026 | 07.09.2026 | |

---

## Kapsanan kaynak tipleri ve özel durumlar

| # | Kapsanan | Nerede |
|---|---|---|
| 1 | `az-patient` | 8 protokol |
| 2 | **`az-newborn-patient`** (kendi FIN'i yok, anne FIN'i ile) | 50772329, 50847194 |
| 3 | `az-practitioner` | hepsi |
| 4 | `az-encounter` — **yatan** (`IMP`, taburcu tarihli) | 5 protokol |
| 5 | `az-encounter` — **ayaktan** (`AMB`) | 50831224, 50841776, 50845123 |
| 6 | `az-encounter` — **günübirlik** | 50859592, 50862376 |
| 7 | `az-condition` (çok tanılı) | 50831224 (12 tanı) |
| 8 | `az-procedure` (İcbari kodlu) | 11'den 571'e kadar |
| 9 | `az-procedure` — **laboratuvar kalemi** (yeni karar) | lab'ı olan her protokol |
| 10 | `az-lab-result-observation` — **birim + referans aralıklı** | 50772329 (192 sonuç) |
| 11 | Laboratuvar — **panel / alt parametre** | 50830462 (56 farklı LOINC) |
| 12 | `az-radiology-diagnostic-report` (Azerice karakterli RTF) | 50772329 (11 tetkik) |
| 13 | `az-pathology-diagnostic-report` (çok raporlu) | 50833609 (3 rapor) |
| 14 | **Patoloji zinciri** — `Composition` + ICD-O-3 `Observation` | 50830462 (2 farklı morfoloji) |
| 15 | **Patoloji — neoplazma yok** (zincir bilerek kurulmaz) | 50862376 |
| 16 | `az-discharge-summary` (Epikriz, kilitli) | 4 protokol |

---

## Kontrol sırasında dikkat edilecek iki nokta

### 50862376 — patoloji zinciri BİLEREK kurulmuyor

Bu protokolün patoloji raporu "neoplazma rastlanmamışdır" sonucu taşıyor.
`DiagnosticReport` gönderiliyor, ancak `Composition` ve `Observation` **üretilmiyor**.

Bu bir eksiklik değil: `0000/0` geçerli bir ICD-O-3 morfoloji kodu değil ve
`az-pathology-finding` bir **kanser bulgusu** profili. `extension:composition` `0..1`
olduğu için sonuç profile uygun. Açık sorularımızdan biri tam olarak budur --
bu raporlar için beklenen davranışı netleştirmelerini rica ediyoruz.

### 50772329 ve 50847194 — yenidoğan profili

Bu iki hastanın kendi FIN'i henüz yok; `az-newborn-patient` profiliyle, yalnızca
**anne FIN'i** identifier'ı kullanılarak gönderiliyorlar. Portalda arama yapılırken
hastanın kendi FIN'i ile değil anne FIN'i ile aranmalıdır.

Bu hastalar ileride kendi FIN'lerini aldığında beklenen davranışı sorduğumuz açık soru
da burada somutlaşıyor: aynı kaydı `az-patient` profiline güncellemeli miyiz, yoksa
farklı bir akış mı gerekiyor?

---

## Kontrol nasıl yapılabilir

1. Her protokol için hasta **FIN'i** üzerinden `Patient` kaydına ulaşılır; yenidoğanlarda
   arama **anne FIN'i** ile yapılmalıdır.
2. Hastanın `Encounter` kayıtlarından ilgili protokole ait olan seçilir -- yukarıdaki
   açılış tarihiyle eşleşir.
3. O `Encounter`'a bağlı `Condition`, `Procedure`, `Observation` ve `DiagnosticReport`
   kayıtları, matristeki sayılarla karşılaştırılır.
4. Patoloji için: `DiagnosticReport` üzerindeki
   `extension:diagnostic-report-composition` takip edilerek `Composition`'a, oradan
   `section.entry` ile ICD-O-3 kodlu `Observation`'lara ulaşılır.
5. Epikriz, ayrı bir `Composition` (`az-discharge-summary`) olarak aynı `Encounter`'a
   bağlıdır.

---

## Nasıl seçildi

Seçim elle değil, kapsama hedefine göre yapıldı: önce en **nadir** özellik (neoplazili
patoloji, ~150 günde sınırlı sayıda uygun protokol) tarandı, bulunan adaylar diğer
özellikleri açısından profillendi, sonra kalan boşluklar (yenidoğan, neoplazisiz patoloji,
ayaktan + epikriz, çok tanılı) hedefli sorgularla dolduruldu.

Bu yüzden liste kısa ama **her satırın bir varlık sebebi var** -- matriste vurgulu
hücreler o protokolü listeye alma nedenini gösterir.
