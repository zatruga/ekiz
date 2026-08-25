# Bölüm (serviceType) eşleştirmesi -- BİREBİR, elle

KARAR (2026-08-20, kullanıcı isteği): Otomatik isim-bazlı eşleştirme + "Digər"(999)
fallback yaklaşımı TERK EDİLDİ.

**GÜNCELLEME:** Eşleştirme artık bu dosyayı elle düzenlemek yerine web panelinden
yapılıyor -- **Bölüm Eşleştirme** sayfası (`/BolumEslestirme`, sidebar > Sistem).
Aşağıdaki iki liste hâlâ referans olarak duruyor (özellikle AZ kod listesi için), ama
gerçek eşleştirme verisi artık `BolumMapping` (SQLite) tablosunda -- bu dosyadaki
üçüncü sütun güncel tutulmuyor. `EncounterMapper.Map` artık bu tabloyu kullanıyor;
eşleşmeyen bir bölüm gelirse (haritada yoksa) SKIPPED olarak loglanıyor, tahmini bir
koda düşürülmüyor.

## 1. AZ hospital-departments CodeSystem (bakanlık, 51 kod)

Kaynak: `http://fhir.az/CodeSystem/hospital-departments` (terminology API'den çekildi,
bkz. `docs/sql-exports/cs_hospital-departments.json`).

| Kod | Ad (AZ) |
|---|---|
| 2 | Pulmonologiya |
| 3 | Revmatologiya |
| 4 | Kardiologiya |
| 5 | Cərrahiyyə |
| 6 | Qastroenterologiya |
| 7 | Endokrinologiya |
| 8 | Allerqologiya-İmmunologiya |
| 9 | Böyüklər üçün yoluxucu xəstəliklər |
| 10 | Uşaqlar üçün yoluxucu xəstəliklər |
| 15 | Travmatologiya (ortopedik) |
| 16 | Urologiya |
| 17 | Onkologiya |
| 18 | Radiologiya |
| 19 | Stomatologiya |
| 21 | Mama-ginekologiya |
| 24 | Oftalmologiya |
| 25 | Otolarinqologiya |
| 26 | Surdologiya |
| 27 | Vərəm |
| 29 | Nevrologiya |
| 30 | Ruhi xəstəliklər |
| 31 | Psixoterapiya |
| 32 | Psixoendokrinologiya |
| 33 | Narkologiya |
| 34 | Yeniyetmələr üçün narkologiya |
| 35 | Anonim müalicə üçün narkologiya |
| 36 | Dəri-zöhrəvi |
| 37 | Bərpaedici müalicə |
| 47 | Qanköçürmə |
| 48 | Hemodializ |
| 49 | Hemosorbsiya |
| 51 | Terapiya |
| 52 | Pediatriya |
| 53 | Reanimasiya |
| 100 | Təcili yardım |
| 101 | Laboratoriya |
| 102 | Nefrologiya |
| 103 | Hematologiya |
| 104 | Üz-çənə cərrahiyyəsi |
| 105 | Toksikologiya |
| 106 | Loqopediya |
| 107 | Genetika |
| 108 | Gerontologiya |
| 109 | Dietologiya |
| 110 | İmmunologiya |
| 111 | Epidemiologiya |
| 112 | Neonatologiya |
| 113 | Ürək-damar cərrahiyyəsi |
| 114 | Neyrocərrahiyyə |
| 115 | Uşaq cərrahiyyəsi |
| 999 | Digər |

## 2. Pusula Ortak.Bolum -- gerçekte kullanılan bölümler (son 365 gün)

Kaynak: `hasta.protokol.BolumId` üzerinden gerçek kullanım (DB'den canlı sorgulandı,
2026-08-20). `Ortak.Bolum` toplam 440 satır ama bunların sadece 65'i son bir yılda
gerçekten kullanılmış -- kullanılmayan ~375 satır bu listeye alınmadı (gelecekte
yeni bir bölüm kullanılırsa loglanıp elle eklenecek). Adet sütunu en çok kullanılana
göre sıralı -- önce üsttekileri eşleştirmek en çok etkiyi yapar.

| Pusula BolumId | Pusula Adı | Adet (365 gün) | AZ Kodu (doldurulacak) |
|---|---|---|---|
| 761 | Onkologiya | 28336 | |
| 36 | Mamalıq-Ginekologiya | 23191 | |
| 25 | Uşaq Sağlamlığı və Xəstəlikləri | 19447 | |
| 34 | Oftalmologiya | 18763 | |
| 37 | Kardiologiya | 11588 | |
| 490 | Radiologiya | 11190 | |
| 460 | Laboratoriya | 10792 | |
| 30 | Qastroenterologiya | 8346 | |
| 35 | Daxili Xəstəliklər | 6914 | |
| 41 | Travmatologiya və Ortopediya | 6575 | |
| 45 | Urologiya | 6367 | |
| 31 | Ümumi Cərrahiyyə | 6310 | |
| 40 | Nevrologiya | 6106 | |
| 1553 | Anesteziologiya və İntensiv Terapiya (GYB) | 5432 | |
| 344 | Endokrinologiya | 4715 | |
| 19 | Təcili Tibbi yardım | 4535 | |
| 401 | Hematologiya | 4445 | |
| 26 | Dermatologiya | 4067 | |
| 442 | Süni Mayalanma | 4037 | |
| 385 | Check Up | 3686 | |
| 29 | Fizioterapiya və Rehabilitasiya | 3334 | |
| 1640 | Uşaq və Yeniyetmə Psixiatriyası | 3246 | |
| 33 | Pulmonologiya | 3014 | |
| 4 | Qulaq-Burun-Boğaz | 3005 | |
| 1590 | Pediatriya (növbətçi) | 2944 | |
| 389 | Uşaq Kardiologiya | 2026 | |
| 415 | Uşaq Hematologiya və Onkologiyası | 1961 | |
| 345 | Revmatologi | 1959 | |
| 22 | Neyrocərrahiyyə | 1936 | |
| 28 | İnfeksion Xəstəliklər | 1899 | |
| 1146 | Histopatologiya | 1785 | |
| 20 | Stomatologiya | 1760 | |
| 39 | Nefrologiya | 1536 | |
| 43 | Psikiyatri | 1299 | |
| 24 | Uşaq Nevrologiyası | 1250 | |
| 42 | Plastik, Estetik və Rekonstruktiv Cərrahiyyə | 1213 | |
| 23 | Uşaq Cərrahiyyə | 1178 | |
| 781 | İnvaziv Radyologiya | 1146 | |
| 1565 | Metabolik - Bariatrik Cərrahiyyə | 1037 | |
| 38 | Ürək - Damar Cərrahiyyəsi | 767 | |
| 32 | Torakal Cerrahiyyə | 681 | |
| 828 | Uşaq Allerqologiyası və İmmunologiyası | 664 | |
| 1575 | Evdə Tibbi xidmət | 645 | |
| 27 | Dietologiya | 569 | |
| 830 | İşyeri həkimi | 527 | |
| 403 | Psixoloq | 430 | |
| 483 | Kosmetoloq | 369 | |
| 559 | Uşaq və Yeniyetmə Psixologiyası | 321 | |
| 1159 | Çocuk Yoğunbakım | 309 | |
| 928 | Uşaq Endokrinologiya | 231 | |
| 1168 | Uşaq Ürək - Damar Cərrahiyyəsi | 154 | |
| 1154 | Alqologiya | 146 | |
| 1566 | Audiologiya | 121 | |
| 1620 | Kök hüceyrə | 83 | |
| 1596 | Süd vəzi xəstəlikləri və cərrahiyyəsi | 49 | |
| 1572 | Pediatrik Təcili Yardım | 24 | |
| 1158 | Palyatif Bakım Kliniği | 22 | |
| 769 | Saç əkimi | 7 | |
| 1170 | Tıbbi Mikrobiyoloji | 2 | |
| 21 | Anestezi ve Reanimasyon (GYB) TR. | 2 | |
| 1549 | Orqan Trasplantasiyası | 1 | |
| 1153 | Uşaq və Neonatalogiya | 1 | |
| 1615 | xxx | 1 | |
| 763 | Neonatologiya | 1 | |
| 930 | Uşaq Qastroenterologiyası | 1 | |

## 3. Notlar

- Bazı Pusula bölümlerinin AZ listesinde net bir karşılığı yok (örn. "Süni Mayalanma",
  "Check Up", "İşyeri həkimi", "Kosmetoloq", "Kök hüceyrə", "Saç əkimi", "xxx"). Bunlar
  için ya en yakın AZ kategorisi seçilecek ya da bilinçli olarak "Digər"(999) verilecek
  -- ikisi de elle, kullanıcı kararı.
- `1615 | xxx | 1` muhtemelen Pusula'da hatalı/test amaçlı girilmiş bir kayıt (tek
  kullanım) -- muhtemelen "Digər"(999) veya yok sayılabilir.
- Tablo dolduktan sonra bana geri iletin (bu dosyayı düzenleyip veya mesajla), ben
  `EncounterMapper.ResolveDepartment`'ı bu sabit eşleştirmeyi kullanacak şekilde
  yeniden yazarım.
