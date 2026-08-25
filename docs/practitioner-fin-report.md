# Doktor FIN Format Raporu

**Tarih:** 2026-08-20
**Kaynak:** Pusula `IK.Personel.TCKimlikNo`, salt-okunur sorgu (son 365 günde `hasta.protokol.DoktorId` olarak gerçekten kullanılmış doktorlar, `PersonelTipiId=1`).
**Amaç:** Practitioner (doktor) senkronunda AZ e-Health'in beklediği FIN formatına (`^[A-Z0-9]{7}$`, 7 alfanumerik karakter) uymayan kayıtları tespit etmek. Production'da gerçek bir gönderim denemesinde ("FATMA NUR TAŞKIN İBADOV", HTTP 400 -- *"FIN must be 7 alphanumeric characters"*) ortaya çıkan sorunun kapsamını ölçmek için yapıldı.

## Özet

| | |
|---|---|
| Son 365 günde protokolde kullanılan farklı doktor sayısı | **246** |
| AZ FIN formatına uymayan | **42 (%17)** |

Bu kayıtlar sistemi bozmuyor -- Practitioner gönderimi `Failed` olarak loglanıyor, ilgili protokolün Encounter'ı doktor bağlantısı (`participant`) olmadan yine de gönderiliyor. Ancak bu doktorlar için Practitioner kaydı **hiç oluşmuyor**.

## Grup 1 -- Muhtemelen gerçek doktorlar, Türkiye T.C. Kimlik No girilmiş (~28 kişi)

11 haneli, tamamen sayısal değerler -- isimlerden de anlaşılan gerçek, bireysel doktor kayıtları. Muhtemelen Liv Bona Dea'da çalışan Türk vatandaşı doktorlar; Pusula'ya T.C. Kimlik No girilmiş, AZ FIN'leri (varsa) hiç kaydedilmemiş.

| Id | Ad Soyad | Pusula'daki değer |
|---|---|---|
| 3431 | Deniz Tümay Albayrak | 25849820862 |
| 3149 | Merve Hakan | 34517050982 |
| 3321 | Mete Karatay | 18892972916 |
| 3543 | Veysel Kerem Bıkmaz | 18130479856 |
| 3399 | Hüseyin Cavit Aydoğdu | 13796206778 |
| 3602 | Fatma Nur Taşkın İbadov | 23152485358 |
| 1369 | Vedat Kaya | 18349818484 |
| 3482 | Kurtuluş Yıldız | 22303567042 |
| 3542 | Serdar Çelik | 12782047012 |
| 968 | Çağatay Öztürk | 85469542285 |
| 2838 | Murat 1 Zor | 16808006526 |
| 2840 | Bahadır 1 Topuz | 14375532516 |
| 3478 | Murat Zor *(muhtemelen 2838 ile aynı kişi, ikinci kayıt)* | 16808006526 |
| 3479 | Bahadır Topuz *(muhtemelen 2840 ile aynı kişi, ikinci kayıt)* | 14375532516 |
| 2744 | Selime Aydoğdu | 13745208426 |
| 3735 | Mehmet Akif Yeşilipek | 65161381 |
| 1154 | Cihangir Mutlu Ercan | 57280447872 |
| 3330 | Olcay Turgut | 21589548912 |
| 3197 | Can Leblebici | 687862269 |
| 3051 | Ender Sir | 11048265108 |

## Grup 2 -- Kişi değil, nöbetçi/vardiya yer tutucu kayıtlar (~8 kişi)

İsimlerinden anlaşılıyor ki bunlar gerçek bir bireyin kaydı değil, "bu vardiyadaki nöbetçi doktor" için genel bir personel kaydı -- TCKimlikNo alanına da anlamsız/rastgele sayılar girilmiş.

| Id | Ad Soyad | Pusula'daki değer |
|---|---|---|
| 3404 | Növbətçi Mamalıq (A.Y.) | 5371468168 |
| 3408 | Növbətçi Mamalıq (N.Q.) (N.Q.) | 689766 |
| 3424 | Növbetçi (L.Z.) | 234234234 |
| 3425 | Növbətçi Kardioloq (A.N.) | 43532452 |
| 3430 | Növbətçi Kardioloq (Ü.N.) | 34564437 |
| 3439 | Növbətçi Kardioloq (N.V.) | 3423423424 |
| 951 | Köçürme Hekimi | 85469542275 |
| 1274 | Embriyolog Süni Mayalama | 85469542375 |
| 3778 | Sevil Məmmədova(SA) | 234234234234 |
| 3042 | Şəmsi Axundov | xxxxxx |

**Öneri:** Bunlar gerçek bir kişiyi temsil etmediği için Practitioner olarak hiç gönderilmemeleri daha doğru olur -- isterseniz bu kayıtları (Id listesi yukarıda) Practitioner senkronundan tamamen hariç tutacak bir filtre eklerim.

## Grup 3 -- Kısa/bozuk kodlar (~7 kişi)

Ne AZ FIN (7 karakter, büyük harf) ne T.C. Kimlik No (11 haneli) formatına uyan, muhtemelen eksik/hatalı veri girişi.

| Id | Ad Soyad | Pusula'daki değer | Sorun |
|---|---|---|---|
| 991 | Seda Erçetin | 43BF07 | 6 karakter (7 olmalı) |
| 1468 | Aynura Alekberova | 3F4F77 | 6 karakter |
| 2566 | Aynur Cavadova | 2BHHM2S. | 8 karakter, sonda nokta |
| 3233 | Aytən Rəsulzadə | 5HX6LT9 (boşluklu) | 7 karakter ama sonda boşluk var |
| 13 | Stevan Tekiç | 226D5B | 6 karakter |
| 2824 | Engin Kaya | 474EC2 | 6 karakter |
| 2839 | Sercan Yılmaz | 47224D | 6 karakter |
| 2750 | Elnurə Vəliyeva | FFDD6 | 5 karakter |
| 3481 | Elnurə Vəliyeva *(muhtemelen 2750 ile aynı kişi)* | FFDD6 | 5 karakter |
| 3331 | Solmaz Əliyeva | 2fm8dd3 | küçük harf (büyük olmalı) |
| 81 | Khorolsuren Orgodol | F54A1 | 5 karakter |
| 1153 | Nazlı Ercan | 44AB78 | 6 karakter |

## Önerilen sonraki adımlar

1. **Grup 1:** Bu doktorların gerçek AZ FIN'i varsa (yabancı çalışma izni/ikamet süreciyle birlikte verilmiş olabilir), İK'dan alınıp Pusula'ya işlenmesi en doğru çözüm -- o zaman sistem otomatik olarak doğru gönderecek.
2. **Grup 2:** Practitioner senkronundan tamamen hariç tutulmalarını öneriyorum (kişi değiller). Onay verirseniz filtreyi eklerim.
3. **Grup 3:** Muhtemelen veri girişi hatası -- İK/Pusula tarafında düzeltilmesi gerekir, sistem tarafında yapılabilecek bir şey yok (format açıkça geçersiz).

Bu liste sistemin `Failed` loglarından da (Aktivite Akışı → Practitioner → Hatalı filtresi) takip edilebilir; yeni bir doktor bu duruma düşerse orada görünecektir.
