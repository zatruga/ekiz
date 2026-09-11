# PACS Erişim Talebi -- AZ e-Health `ImagingStudy` için

**Amaç:** Azerbaycan e-Health'e `az-imaging-study` kaynağı gönderebilmek. Profil iki
kimliği de **zorunlu** kılıyor ve bunlardan biri Pusula'da yok.

**Tarih:** 2026-09-11 · **Durum:** PACS ekibinden erişim bekleniyor

---

## Neden PACS'a ihtiyaç var

`az-imaging-study` profilinin zorunlu alanları ve kaynakları:

| Alan | Kardinalite | Kaynak | Durum |
|---|---|---|---|
| `identifier` (ACSN -- Accession Number) | 1..1 | Pusula | ✅ **Var** |
| `identifier` (UID -- Study Instance UID, `urn:dicom:uid`) | 1..1 | **PACS** | ❌ Pusula'da yok |
| `modality` | 1..* | **PACS** | ❌ Pusula kolonu boş |
| `subject` (Patient) | 1..1 | Pusula | ✅ Var |
| `extension:local-system-unique-id` | 1..1 | Kendimiz üretiyoruz | ✅ |
| `numberOfInstances` | 0..1 | Pusula (`GoruntuSayisi`) | ✅ %79 dolu |
| `encounter`, `started` | 0..1 | Pusula | ✅ Var |

Yani PACS'tan yalnızca **iki şey** gerekiyor: **Study Instance UID** ve **modalite**.

---

## Eşleştirme anahtarı çözüldü: Accession Number

Pusula, PACS'a gönderdiği HL7 `ORM^O01` siparişinde `OBR-2` (Placer Order Number)
alanına şu formatta bir numara yazıyor:

```
OBR|1|BAK393439||199111^MR, beyin, kontrastsiz^MR|...
```

Bu numara Pusula'da şöyle üretiliyor (canlı veride doğrulandı, 2026-09-11):

```
AccessionNumber = RIS.TetkikIslem.AccessionNoEkKarakter + RIS.TetkikIslem.Id
                = "BAK" + 393439
                = "BAK393439"
```

`AccessionNoEkKarakter` 270.074 kayıtta dolu ve tek değer: **`BAK`**.
(`AccessionNo` kolonu boş görünüyor -- Pusula bu numarayı saklamıyor, gönderirken
anlık birleştiriyor. Biz de aynı şekilde üretebiliyoruz.)

**Sonuç:** Accession Number'ı Pusula'dan üretebiliyoruz; PACS'a bununla sorup
Study Instance UID'yi alabiliriz. Bulanık eşleştirmeye (hasta + tarih + modalite)
gerek yok.

---

## PACS ekibinden istenenler

### 1. Tercih edilen: DICOMweb (QIDO-RS) okuma erişimi

Modern ve en kolay yol. Gereken:

- **Base URL** (örn. `https://pacs.hastane.local/dicom-web`)
- **Kimlik doğrulama yöntemi**: Basic / Bearer token / mTLS -- hangisiyse, kullanıcı
  ve parola veya sertifika
- Erişecek sunucunun beyaz listeye alınması: **uygulama sunucusu `10.10.201.57`**
- Güvenlik duvarı kuralı (giden HTTPS)

Yapacağımız tek sorgu tipi:

```http
GET {base}/studies?AccessionNumber=BAK393439
     &includefield=0020000D      # StudyInstanceUID
     &includefield=00080061      # ModalitiesInStudy
     &includefield=00201206      # NumberOfStudyRelatedSeries
     &includefield=00201208      # NumberOfStudyRelatedInstances
Accept: application/dicom+json
```

**Sadece okuma yeterli.** `WADO-RS` (görüntü indirme) veya `STOW-RS` (yazma)
istemiyoruz -- görüntülerin kendisini taşımıyoruz, yalnızca referansını.

### 2. DICOMweb yoksa: DIMSE C-FIND

Klasik DICOM yolu. Gereken:

- PACS **AE Title**, **host/IP**, **port**
- Bizim AE Title'ımızın PACS'ta tanımlanması (örn. `PUSULA_EHEALTH`)
- Güvenlik duvarı kuralı (belirtilen port)

Sorgu: *Study Root Query/Retrieve Information Model – FIND*, anahtar
`AccessionNumber`, istenen alanlar `StudyInstanceUID`, `ModalitiesInStudy`.

### 3. Hangi sistem sorgulanmalı?

Hastanede üç entegrasyon canlı görünüyor (`Ortak.Hl7Mesaj`, hepsinde bugün trafik):

| Entegrasyon | Mesaj sayısı |
|---|---:|
| `FUJIPACSMP` (Fuji PACS) | 768.148 |
| `TELETIP` | 298.547 |
| `VNA` (Vendor Neutral Archive) | 6.077 |

**Sorulacak:** Study Instance UID sorgusu için hangisi doğru uç nokta -- Fuji PACS mı,
VNA mı? Uzun vadeli arşiv VNA ise sorguyu oraya yöneltmek daha doğru olabilir.

### 4. Doğrulama için bir örnek

Erişim açıldığında test edebilmek için bilinen bir accession numarası yeterli.
Örnek (11.09.2026 tarihli, gerçek kayıtlar):

| Accession | Modalite | Görüntü sayısı |
|---|---|---:|
| `BAK392082` | MR, üst abdomen, kontrastlı | 2683 |
| `BAK393394` | MR, multiparametrik prostat | 148 |
| `BAK393413` | Akciğer P.A. (DX) | 1 |

---

## Erişim geldikten sonra bizde yapılacaklar

1. `PacsClient` (QIDO-RS ya da C-FIND) -- accession → Study Instance UID + modalite
2. `ImagingStudyMapper` -- `az-imaging-study` kaynağını üretir
3. `RadiologyReportSyncService` içine bağlanır: rapor gönderilirken ImagingStudy de
   gönderilir, `DiagnosticReport.imagingStudy` ile ilişkilendirilir
4. UID bulunamayan tetkikler **atlanır** (uydurma UID gönderilmez) ve senkron
   günlüğüne nedeni yazılır -- projedeki diğer zorunlu alan kurallarıyla aynı ilke

**Not:** Bakanlığa "ImagingStudy bizden bekleniyor mu, bekleniyorsa görüntülerin
kendisi mi yoksa yalnızca çalışma referansı mı?" sorusu da soruldu
(bkz. `docs/bakanlik-sorulari.md`). Cevap "görüntüler de gönderilecek" olursa bu
talebin kapsamı genişler (WADO-RS + muhtemelen `Binary`/`Endpoint` kaynakları).
