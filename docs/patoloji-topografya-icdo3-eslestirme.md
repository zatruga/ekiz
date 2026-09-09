# Patoloji Topografya Kodu Eşleştirmesi (Pusula SKRS → ICD-O-3)

**Amaç:** AZ e-Health `az-pathology-finding` profili, `component:topography`'de gerçek
ICD-O-3 (XBT-O-3) topografya kodu (örn. `C50.9`) zorunlu kılıyor (1..1, required binding
"XBT-O-3 Topoqrafiya Kodları Value Set" -- doğrulandı, bkz. fhir.e-health.gov.az
StructureDefinition-az-pathology-finding.json, 2026-09-03). Pusula bu bilgiyi
`EMR.Pathology.EPulse.YerlesimYeriCode` alanında tutuyor ama bu, Türkiye'nin kendi ulusal
referans sunucusunun ("SKRS") iç kodu -- ICD-O-3 formatında değil. Pusula veritabanında bu
kodu ICD-O-3'e çeviren bir tablo yok (`Skrs.YerlesimYeri.TopografikKodu` ve
`Ortak.HizmetPatolojiOzellik.YerlesimYeriSkrsKodu` -- ikisi de bunun için var ama ikisi de
0 satır, hiç doldurulmamış).

**Yöntem:** `EMR.Pathology.EPulse`'de canlı olarak kullanılmış 227 farklı topografya
terimi (2022-09'dan bugüne, ~52.000 patoloji kaydı) çekildi. Her terimin WHO ICD-O-3
topografya listesindeki (C00-C80) karşılığı, alt-kategori yapısına göre (örn. C50.0-C50.9
meme alt bölgeleri, C77.0-C77.9 lenf düğümü bölgeleri) tek tek belirlendi. "BBT" kısaltması
tutarlı olarak ICD-O-3'ün ".9 NOS" ("Başka türlü belirtilmemiş") ekine karşılık geliyor.

**DURUM: TASLAK -- CANLIYA ALINMADAN ÖNCE GÖZDEN GEÇİRİLMELİ.** Yanlış bir eşleme, yanlış
organ/bölge kodu göndermek anlamına gelir -- bu bir tıbbi veri kalitesi sorunu olur. Aşağıda
"Not" sütununda işaretlenmemiş satırlar yüksek güvenle eşleştirildi (kategori yapısı ile tam
tutarlı); "⚠" işaretli satırlar ekstra dikkat ister.

**BU GÖZDEN GEÇİRME TEK GÜVENCE -- sunucu yanlışı yakalamaz.** Canlı sandbox'ta denendi
(2026-09-08): `component:topography` required binding taşımasına rağmen sunucu değer
kümesinde olmayan bir kodu (`C99.9`) ve uydurma bir morfolojiyi (`9999/9`) HTTP 200 ile
KABUL etti. Yani `$validate` yapıyı doğruluyor ama anlamı doğrulamıyor -- "geçti" demesi
bu tablonun doğru olduğuna dair hiçbir kanıt değil. Bkz. docs/bakanlik-sorulari.md.

**KOD BAĞLANTISI (2026-09-08):** bu tablo artık
`src/PusulaEHealthSync/Mapping/PathologyTopographyMap.cs` içine 227 girdi olarak
aktarıldı ve `PathologyFindingMapper` tarafından kullanılıyor. Tablo değişirse o dosya
da güncellenmelidir (tek yönlü kopya, çalışma anında bu .md okunmuyor).

**DOĞRULANAN BİÇİM (2026-09-08, IG'den):** AZ CodeSystem'i
(`http://fhir.az/CodeSystem/az-icd-o-3`) topografyayı NOKTALI yazıyor (`C50.9`), yani bu
tablodaki biçim doğru. Örnek display'ler de örtüşüyor: `C50.9 = "Süd vəzi, ƏGO"` (ƏGO =
Azerice "NOS"), bu tablodaki "Meme, BBT / Breast, NOS" ile aynı kavram.

**OTOMATİK DOĞRULAMA (2026-09-09) -- tablonun tamamı kaynağa karşı denetlendi.**
`az-icd-o-3` CodeSystem'inin tam JSON'u indirilip (1.545 kavram: 408 topografya,
1.137 morfoloji) üç kontrol yapıldı:

1. **Kod varlığı:** 227 ICD-O-3 kodunun **tamamı** CodeSystem'de mevcut -- 0 eksik.
   (Karşılaştırma için: morfoloji tarafında 359 koddan 18'i eksik, bkz.
   docs/bakanlik-sorulari.md. Topografyada böyle bir açık yok.)
2. **BBT ↔ ƏGO çapraz kontrolü:** Türkçe terimdeki "BBT" ile Azerice display'deki
   "ƏGO" (= NOS) işaretçileri karşılaştırıldı. 227 satırdan 5'i uyuşmuyor
   (C18.9, C76.1, C13.9, C00.2, C69.5) ama beşi de sadece **çeviri üslubu farkı** --
   AZ display'i NOS ekini yazmamış ya da Türkçe terim BBT demeden aynı kavramı
   anlatmış. Kodlar ICD-O-3'te doğru karşılığa oturuyor. Gerçek hata: 0.
3. **⚠ işaretli 6 satır:** IG'deki Azerice display'lerle tek tek doğrulandı ve
   **hepsi doğru** çıktı. Özellikle C40 (ekstremite) / C41 (diğer) ayrımı ve
   `1120→C26.0` "Bağırsaq traktı ƏGO" vs `1122→C26.9` "Mədə-bağırsaq yolu, ƏGO"
   çifti teyit edildi -- "BBT genelde .9'a gider" kuralının tek istisnası ve doğru.

Bu üç kontrol kodların **var olduğunu ve yapısal olarak tutarlı olduğunu** gösteriyor;
her terimin **tıbben doğru organa** eşlendiği hâlâ insan gözü ister -- tablodaki
"AZ CodeSystem karşılığı" sütunu tam bu iş için eklendi (Türkçe terimle yan yana
okunabilsin diye). "Gönderiliyor ✅" sütunu ise canlı veride neoplazili raporlarda
fiilen kullanılan 158 kodu işaretler -- işaretsiz 69 satır bugün hiçbir şey
göndermiyor, öncelik onlarda değil.

**KAPSAM ÖLÇÜMÜ (2026-09-08):** onaylı + neoplazili raporlarda fiilen geçen **158 farklı**
topografya kodunun TAMAMI bu tabloda mevcut -- canlı veride tek bir açık yok. (Tablodaki
227 girdinin geri kalanı, neoplazi olmayan raporlarda geçen kodlar.)

**MORFOLOJİ İÇİN BÖYLE BİR TABLOYA GEREK YOK:** `EPulse.MorfolojiKoduCode` zaten ICD-O-3
biçiminde (`8523/3`) ve AZ CodeSystem'i birebir aynı gösterimi kullanıyor -- doğrudan
geçiyor, çevrilmiyor.

Kod tabanına bağlanmadan önce: bu listedeki bir `SkrsKodu` karşılığı olmayan bir kayıtla
karşılaşılırsa mapper **atlamalı (Skip)**, asla tahmin etmemeli -- 400 karakter filtresindeki
aynı temkinli ilke.

| Sıklık | Gönderiliyor | SkrsKodu | Pusula Terimi (TR) | ICD-O-3 | AZ CodeSystem karşılığı (Azerice) | ICD-O-3 Terimi (EN) | Not |
|---:|:---:|---|---|---|---|---|---|
| 8867 | ✅ | 1212 | Meme, BBT | C50.9 | Süd vəzi, ƏGO | Breast, NOS |  |
| 5244 | ✅ | 1222 | Serviks uteri | C53.9 | Uşaqlıq boynu | Cervix uteri, NOS |  |
| 4143 | ✅ | 1082 | Mide, BBT | C16.9 | Mədə, ƏGO | Stomach, NOS |  |
| 3987 | ✅ | 1255 | Böbrek, BBT | C64.9 | Böyrək, ƏGO | Kidney, NOS |  |
| 3175 | ✅ | 1316 | Abdomen, BBT | C76.2 | Qarın boşluğu, ƏGO | Abdomen, NOS |  |
| 2347 | ✅ | 1143 | Akciğer, BBT | C34.9 | Ağciyər, ƏGO | Lung, NOS |  |
| 2329 | ✅ | 1168 | Kemik iliği | C42.1 | Sümük iliyi | Bone marrow |  |
| 2258 | ✅ | 1097 | Kolon, BBT | C18.9 | Yoğun bağırsaq | Colon, NOS |  |
| 1816 | ✅ | 1329 | Lenf düğümü, BBT | C77.9 | Limfa düyünü, ƏGO | Lymph nodes, NOS |  |
| 1645 | ✅ | 1105 | Karaciğer | C22.0 | Qaraciyər, | Liver |  |
| 1288 | ✅ | 1181 | Deri, BBT | C44.9 | Dəri, ƏGO | Skin, NOS |  |
| 930 | ✅ | 1224 | Endometrium | C54.1 | Endometrium | Endometrium |  |
| 921 | ✅ | 1246 | Prostat bezi | C61.9 | Prostat vəzi | Prostate gland |  |
| 921 | ✅ | 1229 | Uterus, BBT | C55.9 | Uşaqlıq, ƏGO | Uterus, NOS |  |
| 669 | ✅ | 1291 | Beyin, BBT | C71.9 | Beyin, ƏGO | Brain, NOS |  |
| 669 | ✅ | 1166 | Kemik, BBT | C41.9 | Sümük, ƏGO | Bone, NOS |  |
| 619 | ✅ | 1220 | Ekzoserviks | C53.1 | Uşaqlıq boynunun seroz qişası | Exocervix |  |
| 425 | ✅ | 1119 | Pankreas, BBT | C25.9 | Mədəaltı vəzi, ƏGO | Pancreas, NOS |  |
| 415 | ✅ | 1267 | Mesane, BBT | C67.9 | Sidik kisəsi, (sidiklik) ƏGO | Bladder, NOS |  |
| 381 | ✅ | 1230 | Ovaryum | C56.9 | Yumurtalıq | Ovary |  |
| 369 | ✅ | 1302 | Thyroid bezi | C73.9 | Qalxanvari vəzi | Thyroid gland |  |
| 349 | ✅ | 1314 | Baş, yüz ya da boyun, BBT | C76.0 | Baş, üz və boyun, ƏGO | Head, face or neck, NOS |  |
| 347 | ✅ | 1199 | Bağ dokusu, subkutan doku ve diğer yumuşak dokular, BBT | C49.9 | Birləşdirici, dərialtı və digər yumşaq toxumalar, ƏGO | Connective/soft tissue, NOS |  |
| 335 | ✅ | 1150 | Plevra, BBT | C38.4 | Plevra, ƏGO | Pleura, NOS |  |
| 329 | ✅ | 1315 | Göğüs/Toraks | C76.1 | Döş qəfəsi, ƏGO | Thorax, NOS |  |
| 329 | ✅ | 1107 | Safra kesesi | C23.9 | Öd kisəsi | Gallbladder |  |
| 311 | ✅ | 1083 | Duodenum | C17.0 | Onikibarmaq bağırsaq | Duodenum |  |
| 296 | ✅ | 1073 | Özofagus, BBT | C15.9 | Qida borusu, ƏGO | Esophagus, NOS |  |
| 283 | ✅ | 1219 | Endoserviks | C53.0 | Uşaqlıq boynu kanalının selikli qişası | Endocervix |  |
| 274 | ✅ | 1208 | Memenin üst-dış kadranı | C50.4 | Süd vəzinin yuxarı xarici kvadrantı | Upper-outer quadrant of breast |  |
| 265 | ✅ | 1100 | Rektum, BBT | C20.9 | Düz bağırsaq, ƏGO | Rectum, NOS |  |
| 256 | ✅ | 1202 | Periton, BBT | C48.2 | Müsariqə, ƏGO | Peritoneum, NOS |  |
| 225 | ✅ | 1305 | Adrenal bez, BBT | C74.9 | Böyrəküstü vəzi, ƏGO | Adrenal gland, NOS |  |
| 223 | ✅ | 1095 | Sigmoid kolon | C18.7 | Siqmayabənzər çənbər bağırsaq | Sigmoid colon |  |
| 221 | ✅ | 1249 | Testis, BBT | C62.9 | Xaya, ƏGO | Testis, NOS |  |
| 204 | ✅ | 1148 | Mediasten, BBT | C38.3 | Divararalığı, ƏGO | Mediastinum, NOS |  |
| 178 | ✅ | 1076 | Mide korpusu | C16.2 | Mədə cismi | Body of stomach |  |
| 168 | ✅ | 1225 | Miyometrium | C54.2 | Miometrium | Myometrium |  |
| 164 | ✅ | 1191 | Baş, yüz ve boyun bağ dokusu... | C49.0 | Başın, üzün və boynun birləş- dirici, dərialtı və digər yumşaq toxumaları | Connective/soft tissue of head, face, neck |  |
| 162 | ✅ | 1099 | Appendiks | C18.1 | Soxulcanabənzər çıxıntı (appendiks) | Appendix |  |
| 141 | ✅ | 1322 | Baş, yüz ve boyun lenf düğümleri | C77.0 | Başın, üzün və boynun limfa düyünləri | Lymph nodes of head, face, neck |  |
| 141 | ✅ | 1088 | İnce barsak, BBT | C17.9 | Nazik bağırsaq, ƏGO | Small intestine, NOS |  |
| 138 | ✅ | 1217 | Vagina, BBT | C52.9 | Uşaqlıq yolu, ƏGO | Vagina |  |
| 132 | ✅ | 1195 | Abdomen bağ dokusu... | C49.4 | Qarın boşluğunun birləşdirici, dərialtı və digər yumşaq toxumaları | Connective/soft tissue of abdomen |  |
| 130 | ✅ | 1094 | Desendan kolon | C18.6 | Enən çənbər bağırsaq | Descending colon |  |
| 118 | ✅ | 1198 | Bağ dokusu...aşan lezyon | C49.8 | Birləşdirici, dərialtı və digər yumşaq toxumaların yuxarıda qeyd edilən nahiyələrdən birinin və ya bir neçəsinin hüdud- larından kənara çıxan zədələnməsi | Overlapping lesion of connective/soft tissue |  |
| 116 | ✅ | 1090 | Asenden kolon | C18.2 | Qalxan çənbər bağırsaq | Ascending colon |  |
| 110 | ✅ | 1303 | Adrenal bez korteksi | C74.0 | Böyrəküstü vəzin qabıq maddəsi | Cortex of adrenal gland |  |
| 106 |  | 1282 | Spinal meninksler | C70.1 | Onurğa beyninin qişaları | Spinal meninges |  |
| 103 | ✅ | 1138 | Ana bronşlar | C34.0 | Baş bronx | Main bronchus |  |
| 94 | ✅ | 1317 | Pelvis, BBT | C76.3 | Çanaq, ƏGO | Pelvis, NOS |  |
| 87 | ✅ | 1077 | Gastrik antrum | C16.3 | Mədənin çıxacağı mağarası | Gastric antrum |  |
| 87 | ✅ | 1304 | Adrenal bez medullası | C74.1 | Böyrəküstü vəzin beyin maddəsi | Medulla of adrenal gland |  |
| 86 | ✅ | 1068 | Abdominal özofagus | C15.2 | Qida borusunun qarın hissəsi | Abdominal esophagus |  |
| 81 | ✅ | 1325 | Aksilla ya da kol lenf düğümleri | C77.3 | Qoltuq çuxuru və ya yuxarı ətrafların limfa düyünləri | Axillary lymph nodes / upper limb |  |
| 79 | ✅ | 1112 | Pankreas başı | C25.0 | Mədəaltı vəzinin başı | Head of pancreas |  |
| 77 | ✅ | 1071 | Özofagusun 1/3 alt bölümü | C15.5 | Qida borusunun aşağı üçdəbiri | Lower third of esophagus |  |
| 77 | ✅ | 1206 | Memenin üst-iç kadranı | C50.2 | Süd vəzinin yuxarı daxili kvadrantı | Upper-inner quadrant of breast |  |
| 76 | ✅ | 1085 | İleum | C17.2 | Qalça bağırsaq | Ileum |  |
| 73 |  | 1186 | Abdomenin periferik ve otonom sinir sistemi | C47.4 | Qarın boşluğunun periferik sinirləri və vegetativ (avtonom) sinir sistemi | Peripheral/autonomic nerves of abdomen |  |
| 68 | ✅ | 1144 | Timus | C37.9 | Timus | Thymus |  |
| 64 | ✅ | 1319 | Alt ekstremite, BBT | C76.5 | Aşağı ətraf, ƏGO | Lower limb, NOS |  |
| 64 | ✅ | 1193 | Alt ekstremite ve kalça bağ dokusu... | C49.2 | Aşağı ətrafın və budun yuxarı hissəsinin birləşdirici, dərialtı və digər yumşaq toxumaları | Connective/soft tissue of lower limb, hip |  |
| 61 | ✅ | 1092 | Transvers kolon | C18.4 | Köndələn çənbər bağırsaq | Transverse colon |  |
| 52 | ✅ | 1209 | Memenin alt-dış kadranı | C50.5 | Süd vəzinin aşağı xarici kvadrantı | Lower-outer quadrant of breast |  |
| 52 | ✅ | 1136 | Larinks, BBT | C32.9 | Qırtlaq, ƏGO | Larynx, NOS |  |
| 52 | ✅ | 1164 | Pelvik kemik, sakrum, koksiks... | C41.4 | Çanaq sümükləri, oma, büzdüm və aidiyyəti oynaqlar | Pelvic bones, sacrum, coccyx |  |
| 50 | ✅ | 1075 | Mide fundusu | C16.1 | Mədə dibi | Fundus of stomach |  |
| 49 | ✅ | 1235 | Uterin adneks | C57.4 | Uşaqlıq artımları | Uterine adnexa |  |
| 49 | ✅ | 1170 | Dalak | C42.2 | Dalaq | Spleen |  |
| 42 | ✅ | 1034 | Ağız, BBT | C06.9 | Ağız, ƏGO | Mouth, NOS |  |
| 41 | ✅ | 1211 | Memede aşan lezyon | C50.8 | Süd vəzinin yuxarıda qeyd edilən nahiyələrindən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of breast |  |
| 41 | ✅ | 1326 | İnguinal bölge ya da bacak lenf düğümleri | C77.4 | Qasıq nahiyəsi və ya aşağı ətraf limfa düyünləri | Inguinal lymph nodes / lower limb |  |
| 40 | ✅ | 1098 | Rektosigmoid bileşke | C19.9 | Rektosiqmoid birləşmə | Rectosigmoid junction |  |
| 40 | ✅ | 1056 | Nasofarinks, BBT | C11.9 | Burun-udlaq, ƏGO | Nasopharynx, NOS |  |
| 38 | ✅ | 1017 | Dil, BBT | C02.9 | Dil, ƏGO | Tongue, NOS |  |
| 37 | ✅ | 1156 | Alt ekstremite uzun kemikleri... | C40.2 | Aşağı ətrafların uzun borulu sümükləri və aidiyyəti oynaqlar | Long bones of lower limb |  |
| 35 | ✅ | 1162 | Vertebral kolon/Omurga | C41.2 | Onurğa sütunu | Vertebral column |  |
| 33 | ✅ | 1205 | Memenin santral bölgesi | C50.1 | Süd vəzinin mərkəzi hissəsi | Central portion of breast |  |
| 30 | ✅ | 1179 | Alt ekstremite ve kalça derisi | C44.7 | Bud-çanaq nayihəsinin və aşağı ətrafın dərisi' | Skin of lower limb, hip |  |
| 30 | ✅ | 1102 | Anal kanal | C21.1 | Anal kanal | Anal canal |  |
| 27 | ✅ | 1113 | Pankreas gövdesi | C25.1 | Mədəaltı vəzinin cismi | Body of pancreas |  |
| 27 | ✅ | 1157 | Alt ekstremite kısa kemikleri... | C40.3 | Aşağı ətrafların qısa borulu sümükləri və aidiyyəti oynaqlar | Short bones of lower limb |  |
| 26 | ✅ | 1177 | Gövde derisi | C44.5 | Gövdə dərisi | Skin of trunk |  |
| 25 | ✅ | 1079 | Mide küçük kurvatur, BBT | C16.5 | Mədənin kiçik əyriliyi, ƏGO | Lesser curvature of stomach, NOS |  |
| 24 | ✅ | 1318 | Üst ekstremite, BBT | C76.4 | Yuxarı ətraf, ƏGO | Upper limb, NOS |  |
| 24 | ✅ | 1145 | Kalp | C38.0 | Ürək | Heart |  |
| 23 | ✅ | 1089 | Çekum | C18.0 | Kor bağırsaq | Cecum |  |
| 22 | ✅ | 1070 | Özofagusun 1/3 orta bölümü | C15.4 | Qida borusunun orta üçdəbiri | Middle third of esophagus |  |
| 21 | ✅ | 1080 | Mide büyük kurvatur, BBT | C16.6 | Mədənin böyük əyriliyi, ƏGO | Greater curvature of stomach, NOS |  |
| 21 | ✅ | 1154 | Üst ekstremite uzun kemikleri, skapula... | C40.0 | Yuxarı ətrafların uzun borulu sümükləri, kürək sümüyü və aidiyyəti oynaqlar | Long bones of upper limb, scapula |  |
| 21 | ✅ | 1069 | Özofagusun 1/3 üst bölümü | C15.3 | Qida borusunun yuxarı üçdəbiri | Upper third of esophagus |  |
| 21 | ✅ | 1192 | Üst ekstremite ve omuz bağ dokusu... | C49.1 | Yuxarı ətrafın və çiyin qurşağının birləşdirici, dərialtı və digər yumşaq toxumaları | Connective/soft tissue of upper limb, shoulder |  |
| 20 | ✅ | 1109 | Vater ampullası | C24.1 | Fater ampulası və ya hepatopankreatik axacaq | Ampulla of Vater |  |
| 20 | ✅ | 1200 | Retroperitoneum | C48.0 | Peritonarxası sahə | Retroperitoneum |  |
| 20 | ✅ | 1197 | Gövde bağ dokusu... BBT | C49.6 | Gövdənin birləşdirici, dərialtı və digər yumşaq toxumaları, ƏGO | Connective/soft tissue of trunk, NOS |  |
| 19 | ✅ | 1104 | Rektum, anus ve anal kanalda aşan lezyon | C21.8 | Düz bağırsaq, anus və anal kanalın yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of rectum/anus/anal canal |  |
| 19 | ✅ | 1257 | Üreter | C66.9 | Sidik axarı | Ureter |  |
| 19 | ✅ | 1228 | Korpus uteri | C54.9 | Uşaqlıq cismi | Corpus uteri, NOS |  |
| 18 | ✅ | 1204 | Memebaşı | C50.0 | Süd vəzinin giləsi | Nipple |  |
| 16 | ✅ | 1074 | Kardia, BBT | C16.0 | Kardiya, ƏGO | Cardia, NOS |  |
| 16 |  | 1043 | Tonsil, BBT | C09.9 | Badamcıq, ƏGO | Tonsil, NOS |  |
| 16 | ✅ | 1084 | Jejunum | C17.1 | Acı bağırsaq | Jejunum |  |
| 16 | ✅ | 1218 | Vulva, BBT | C51.9 | Vulva, ƏGO | Vulva, NOS |  |
| 15 | ✅ | 1111 | Safra yolları, BBT | C24.9 | Öd yolu, ƏGO | Biliary tract, NOS |  |
| 14 |  | 1280 | Göz, BBT | C69.9 | Göz, ƏGO | Eye, NOS |  |
| 14 | ✅ | 1324 | İntra-abdominal lenf düğümleri | C77.2 | Qarındaxili limfa düyünləri | Intra-abdominal lymph nodes |  |
| 14 | ✅ | 1207 | Memenin alt-iç kadranı | C50.3 | Süd vəzinin aşağı daxili kvadrantı | Lower-inner quadrant of breast |  |
| 13 | ✅ | 1203 | Retroperiton ve peritonda aşan lezyon | C48.8 | Peritonarxası və peritonun yuxarıda qeyd edilən nahiyələrindən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of retroperitoneum/peritoneum |  |
| 13 | ✅ | 1035 | Parotis bezi | C07.9 | Qulaqaltı vəzi | Parotid gland |  |
| 12 | ✅ | 1165 | Kemik, eklem ve eklem kıkırdaklarında aşan lezyon | C41.8 | Sümük, oynaq və oynaq qığırdaqlarının yuxarıda qeyd edilən nahiyələrdən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of bone/joint/cartilage | ⚠ |
| 12 | ✅ | 1210 | Memenin aksiller kuyruğu | C50.6 | Süd vəzinin qoltuq çıxıntısı | Axillary tail of breast |  |
| 11 | ✅ | 1176 | Baş saçlı deri ve boyun derisi | C44.4 | Baş və boyun dərisi | Skin of scalp and neck |  |
| 11 | ✅ | 1226 | Fundus uteri | C54.3 | Uşaqlıq cismi dibi | Fundus uteri |  |
| 11 | ✅ | 1142 | Akciğerde aşan lezyon | C34.8 | Ağciyərin yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of lung |  |
| 11 | ✅ | 1101 | Anus, BBT | C21.0 | Anus, ƏGO | Anus, NOS |  |
| 11 | ✅ | 1091 | Kolonun hepatik fleksuru | C18.3 | Qalxan çənbər bağırsağın qaraciyər əyriliyi | Hepatic flexure of colon |  |
| 11 | ✅ | 1194 | Toraks bağ dokusu... | C49.3 | Döş qəfəsinin birləşdirici, dərialtı və digər yumşaq toxumaları | Connective/soft tissue of thorax |  |
| 10 | ✅ | 1155 | Üst ekstremite kısa kemikleri... | C40.1 | Yuxarı ətrafların qısa borulu sümükləri və aidiyyəti oynaqlar | Short bones of upper limb |  |
| 10 |  | 1182 | Baş, yüz, boyun periferik ve otonom sinir sistemi | C47.0 | Başın, üzün və boynun periferik sinirləri və vegetativ sinir sistemi | Peripheral/autonomic nerves of head, face, neck |  |
| 10 |  | 1293 | Kauda ekuina | C72.1 | At quyruğu | Cauda equina |  |
| 10 |  | 1307 | Hipofiz bezi | C75.1 | Əzgiləbənzər vəzi | Pituitary gland |  |
| 10 | ✅ | 1009 | Dudak, BBT | C00.9 | Dodaq ƏGO | Lip, NOS |  |
| 9 | ✅ | 1114 | Pankreas kuyruğu | C25.2 | Mədəaltı vəzinin quyruğu | Tail of pancreas |  |
| 9 | ✅ | 1052 | Nasofarinks posteriyör duvarı | C11.1 | Udlağın burun hissəsinin arxa divarı | Posterior wall of nasopharynx |  |
| 9 | ✅ | 1163 | Kosta, sternum, klavikula... | C41.3 | Qabırğalar, döş sümüyü, körpücük sümüyü və aidiyyəti oynaqlar | Rib, sternum, clavicle |  |
| 9 | ✅ | 1078 | Pilor | C16.4 | Mədə çıxacağı | Pylorus |  |
| 8 | ✅ | 1284 | Serebrum/beyin | C71.0 | Baş beyin | Cerebrum |  |
| 8 | ✅ | 1174 | Dış kulak | C44.2 | Xarici qulaq | Skin of ear and external auricular canal |  |
| 8 | ✅ | 1173 | Göz kapağı | C44.1 | Göz qapağı | Skin of eyelid, including canthus |  |
| 8 | ✅ | 1260 | Mesane yan duvarı | C67.2 | Sidik kisəsinin divarı | Lateral wall of bladder |  |
| 8 | ✅ | 1201 | Peritonun spesifik bölgeleri | C48.1 | Peritonun qeyd edilən hissələri | Specified parts of peritoneum | ⚠ |
| 7 | ✅ | 1031 | Ağız vestibülü | C06.1 | Ağız dəhlizi | Vestibule of mouth |  |
| 7 | ✅ | 1256 | Renal pelvis | C65.9 | Böyrək ləyəni | Renal pelvis |  |
| 7 |  | 1024 | Ağız tabanı, BBT | C04.9 | Agız boşlugu dibi, ƏGO | Floor of mouth, NOS |  |
| 7 | ✅ | 1320 | Diğer iyi tanımlanmamış bölgeler | C76.7 | Digər dəqiqləşdirilməmiş nahiyələr | Other ill-defined sites |  |
| 7 |  | 1239 | Plasenta | C58.9 | Cift | Placenta |  |
| 7 | ✅ | 1081 | Midede aşan lezyon | C16.8 | Mədənin yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of stomach |  |
| 6 | ✅ | 1221 | Serviks uteride aşan lezyon | C53.8 | Uşaqlıq boynunun yuxarıda qeyd edilən nahiyələrindən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of cervix uteri |  |
| 6 | ✅ | 1093 | Kolonun splenik fleksuru | C18.5 | Çənbər bağırsağın dalaq əyriliyi | Splenic flexure of colon |  |
| 6 | ✅ | 1178 | Üst ekstremite ve omuz derisi | C44.6 | Yuxarı ətraf və çiyin qurşağı dərisi | Skin of upper limb and shoulder |  |
| 6 | ✅ | 1019 | Alt dişeti | C03.1 | Aşağı diş əti | Lower gum |  |
| 6 |  | 1122 | Gastrointestinal kanal, BBT | C26.9 | Mədə-bağırsaq yolu, ƏGO | Gastrointestinal tract, NOS |  |
| 6 |  | 1306 | Parathyroid bezi | C75.0 | Qalxanvari ətraf vəzi | Parathyroid gland |  |
| 5 | ✅ | 1141 | Alt lob, akciğer | C34.3 | Ağciyərin aşağı payı | Lower lobe, lung |  |
| 5 | ✅ | 1327 | Pelvik lenf düğümü | C77.5 | Çanaq limfa düyünləri | Pelvic lymph nodes |  |
| 5 | ✅ | 1216 | Vulvayı aşan lezyonlar | C51.8 | Vulvanın yuxarıda qeyd edilən nahiyələrindən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of vulva |  |
| 5 | ✅ | 1036 | Submandibuler bez | C08.0 | Əngaltı vəzi | Submandibular gland |  |
| 4 |  | 1245 | SKROTUMm, BBT | C63.2 | Xayalıq, ƏGO | Scrotum, NOS |  |
| 4 |  | 1283 | Meninksler, BBT | C70.9 | Beyin qişaları, ƏGO | Meninges, NOS |  |
| 4 |  | 1263 | Mesane boynu | C67.5 | Sidik kisəsi boynu | Bladder neck |  |
| 4 |  | 1286 | Temporal lob | C71.2 | Gicgah payı | Temporal lobe |  |
| 4 |  | 1066 | Servikal özofagus | C15.0 | Qida borusunun boyun hissəsi | Cervical esophagus |  |
| 4 | ✅ | 1175 | Yüzün diğer ve spesifiye edilmeyen bölgelerinin derisi | C44.3 | Üzün digər və qeyd edilməyən nahiyələrinin dərisi | Skin of other/unspecified face parts |  |
| 3 | ✅ | 1146 | Anteriyör mediasten | C38.1 | Divararalığının ön hissəsi | Anterior mediastinum |  |
| 3 |  | 1130 | Aksesuvar sinus, BBT | C31.9 | Burnun əlavə cibi, ƏGO | Accessory sinus, NOS |  |
| 3 | ✅ | 1312 | Endokrin bezleri ve ilgili yapılarda aşan lezyon | C75.8 | Endokrin vəzilər və aidiyyəti strukturların yuxarıda qeyd edilən nahiyələrdən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of endocrine glands |  |
| 3 |  | 1236 | Kadın genital organların diğer spesifik bölgeleri | C57.7 | Qadın cinsiyyət orqanlarının digər qeyd edilən hissələri | Other specified parts of female genital organs | ⚠ |
| 3 |  | 1276 | Orbita, BBT | C69.6 | Göz yuvası, ƏGO | Orbit, NOS |  |
| 3 | ✅ | 1180 | Deride aşan lezyonlar | C44.8 | Dərinin yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi ' | Overlapping lesion of skin |  |
| 3 |  | 1261 | Mesane anteriyor duvarı | C67.3 | Sidik kisəsinin ön divarı | Anterior wall of bladder |  |
| 3 | ✅ | 1149 | Kalp, mediasten ve plevrada aşan lezyon | C38.8 | Ürək, divararalığı və plevranın yuxarıda qeyd edilən nahiyələrdən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of heart/mediastinum/pleura |  |
| 3 |  | 1300 | Beyin ve merkez sinir sisteminde aşan lezyon | C72.8 | Beyin və mərkəzi sinir sisteminin yuxarıda qeyd edilən nahiyələrdən biri və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi' | Overlapping lesion of brain/CNS |  |
| 3 |  | 1106 | İntrehepatik safra yolları | C22.1 | Qaraciyərdaxili (intrahepatik) öd axacağı | Intrahepatic bile duct |  |
| 3 | ✅ | 1134 | Laringeal kıkırdak | C32.3 | Qırtlaq qığırdağı | Laryngeal cartilage |  |
| 2 |  | 1268 | Üretra | C68.0 | Sidik kanalı | Urethra |  |
| 2 |  | 1049 | Orofarinksde aşan lezyon | C10.8 | Udlağın ağız hissəsinin yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of oropharynx |  |
| 2 |  | 1328 | Multipl bölgelerin lenf düğümleri | C77.8 | Bir neçə nahiyənin limfa düyünləri | Lymph nodes of multiple regions |  |
| 2 | ✅ | 1262 | Mesane posteriyor duvarı | C67.4 | Sidik kisəsinin arxa divarı | Posterior wall of bladder |  |
| 2 | ✅ | 1292 | Spinal Kord | C72.0 | Onurğa beyni | Spinal cord |  |
| 2 |  | 1016 | Dilde aşan lezyon | C02.8 | Dilin yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of tongue |  |
| 2 |  | 1126 | Ethmoid sinüs | C31.1 | Xəlbir cibi | Ethmoid sinus |  |
| 2 |  | 1289 | Beyin sapı | C71.7 | Beyin kötüyü | Brain stem |  |
| 2 |  | 1271 | Üriner sistem, BBT | C68.9 | Sidik sistemi, ƏGO | Urinary system, NOS |  |
| 2 |  | 1087 | İnce barsağı aşan lezyonlar | C17.8 | Nazik bağırsağın yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of small intestine |  |
| 2 |  | 1050 | Orofarinks, BBT | C10.9 | Udlağın ağız hissəsi, ƏGO | Oropharynx, NOS |  |
| 2 |  | 1139 | Üst lob, akciğer | C34.1 | Ağciyərin yuxarı payı | Upper lobe, lung |  |
| 2 |  | 1063 | Farinks, BBT | C14.0 | Udlaq, ƏGO | Pharynx, NOS |  |
| 2 |  | 1123 | Nasal kavite | C30.0 | Burun boşluğu | Nasal cavity |  |
| 2 | ✅ | 1244 | Penis, BBT | C60.9 | Penis, ƏGO | Penis, NOS |  |
| 2 |  | 1030 | Yanak mukozası | C06.0 | Yanağın selekli qişası | Cheek mucosa |  |
| 2 |  | 1231 | Fallop tüpü | C57.0 | Fallop borusu | Fallopian tube |  |
| 1 | ✅ | 1108 | Ekstrahepatik safra yolları | C24.0 | Qaraciyərxarici öd axacağı | Extrahepatic bile duct |  |
| 1 |  | 1014 | Dilin 2/3 anteriyoru, BBT | C02.3 | Dilin ön 2/3-si, ƏGO | Anterior two-thirds of tongue, NOS |  |
| 1 |  | 1214 | Labium minus | C51.1 | Kiçik cinsiyyət dodaqları | Labium minus |  |
| 1 |  | 1321 | İyi tanımlanmamış bölgede aşan lezyon | C76.8 | Dəqiqləşdirilməmiş nahiyələrin birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi ' | Overlapping lesion of ill-defined sites |  |
| 1 |  | 1028 | Damakta aşan lezyon | C05.8 | Damağın yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of palate |  |
| 1 |  | 1140 | Orta lob, akciğer | C34.2 | Ağciyərin orta payı | Middle lobe, lung |  |
| 1 |  | 1110 | Safra yollarında aşan lezyon | C24.8 | Öd yolunun yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of biliary tract |  |
| 1 |  | 1010 | Dil tabanı, BBT | C01.9 | Dil kökü, ƏGO | Base of tongue |  |
| 1 | ✅ | 1252 | Diğer spesifiye edilebilen erkek genital organları | C63.7 | Kişi cinsiyyət orqanlarının digər dəqiqləşdirilmiş hissələri | Other specified parts of male genital organs | ⚠ |
| 1 | ✅ | 1227 | Korpus uteride aşan lezyon | C54.8 | Uşaqlıq cisminin yuxarıda qeyd edilən nahiyələrdən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of corpus uteri |  |
| 1 | ✅ | 1161 | Mandibula | C41.1 | Çənə | Mandible |  |
| 1 | ✅ | 1055 | Nasofarinksde aşan lezyon | C11.8 | Burun-udlağın yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of nasopharynx |  |
| 1 |  | 1313 | Endokrin bez, BBT | C75.9 | Endokrin vəziləri, ƏGO | Endocrine gland, NOS |  |
| 1 |  | 1039 | Büyük tükrük bezleri, BBT | C08.9 | Böyük ağız suyu vəzi, ƏGO | Major salivary gland, NOS |  |
| 1 |  | 1067 | Torasik özofagus | C15.1 | Qida borusunun döş hissəsi | Thoracic esophagus |  |
| 1 |  | 1062 | Hipofarinks, BBT | C13.9 | Udlağın aşağı hissəsi | Hypopharynx, NOS |  |
| 1 | ✅ | 1012 | Dilin sınırları | C02.1 | Dil kənarı | Border of tongue |  |
| 1 | ✅ | 1215 | Klitoris | C51.0 | Böyük cinsiyyət dodaqları | Clitoris |  |
| 1 |  | 1120 | Intestinal trakt, BBT | C26.0 | Bağırsaq traktı ƏGO | Intestinal tract, NOS | ⚠ |
| 1 |  | 1269 | Paraürertal bez | C68.1 | Parauretral vəzi | Paraurethral gland |  |
| 1 |  | 1005 | Alt dudak mukozası | C00.4 | Alt dodağın selikli qişası | Lower lip, inner aspect |  |
| 1 | ✅ | 1026 | Yumuşak damak, BBT | C05.1 | Yumşaq damaq,ƏGO | Soft palate, NOS |  |
| 1 |  | 1290 | Beyinde aşan lezyon | C71.8 | Beynin yuxarıda qeyd edilən nahiyələrdən birinin və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of brain |  |
| 1 |  | 1042 | Tonsilde aşan lezyon | C09.8 | Badamcığın yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of tonsil |  |
| 1 | ✅ | 1003 | Dış dudak, BBT | C00.2 | Xarıci dodaq | External lip, NOS |  |
| 1 |  | 1048 | Branchial yarık | C10.4 | Qəlsəmə yarığı | Branchial cleft |  |
| 1 |  | 1045 | Epiglot anterior yüzey | C10.1 | Qırtlaq qapağının ön səthi | Anterior surface of epiglottis |  |
| 1 |  | 1033 | Ağzın diğer ve spesifiye edilemeyen bölgelerinde aşan lezyon | C06.8 | Ağzin digər və qeyd edilməyən hissələrinin yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of mouth |  |
| 1 |  | 1147 | Posteriyor mediasten | C38.2 | Divararalığının arxa hissəsi | Posterior mediastinum |  |
| 1 |  | 1301 | Sinir sistemi, BBT | C72.9 | Sinir sistemi, ƏGO | Nervous system, NOS |  |
| 1 | ✅ | 1020 | Dişeti, BBT | C03.9 | Diş əti, ƏGO | Gum, NOS |  |
| 1 |  | 1117 | Pankreasın diğer spesifiye edilebilen bölgeleri | C25.7 | Mədəaltı vəzinin digər spesifik hissələri | Other specified parts of pancreas |  |
| 1 |  | 1158 | Ekstremite kemikleri, eklemler ve eklem kıkırdaklarında aşan lezyon | C40.8 | Ətrafların sümük, oynaq və oynaq qığırdaqlarının yuxarıda qeyd edilən nahiyələrindən bir və ya bir neçəsinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of limb bones/joints/cartilage | ⚠ |
| 1 |  | 1323 | İntratorasik lenf düğümleri | C77.1 | Döş qəfəsidaxili limfa düyünləri | Intrathoracic lymph nodes |  |
| 1 |  | 1037 | Sublingual bez | C08.1 | Dilaltı vəzi | Sublingual gland |  |
| 1 |  | 1275 | Lakrimal bez | C69.5 | Gözyaşı axacağı, ƏGO | Lacrimal gland |  |
| 1 |  | 1029 | Damak/Palate, BBT | C05.9 | Damaq,ƏGO | Palate, NOS |  |
| 1 | ✅ | 1196 | Pelvis bağ dokusu, subkutan doku ve diğer yumuşak dokular | C49.5 | Çanağın birləşdirici, dərialtı və digər yumşaq toxumaları | Connective/soft tissue of pelvis |  |
| 1 |  | 1213 | Labium majus | C51.2 | Klitor | Labium majus |  |
| 1 |  | 1007 | Dudak komissür | C00.6 | Dodaq bitişməsi | Commissure of lip |  |
| 1 |  | 1001 | Üst dış dudak | C00.0 | Üst dodağın xaric hissəsi | External upper lip |  |
| 1 |  | 1237 | Kadın genital organlarında aşan lezyon | C57.8 | Qadın cinsiyyət orqanlarının yuxarıda qeyd edilən nahiyə- lərdən birinin və ya bir neçə- sinin hüdudlarından kənara çıxan zədələnməsi | Overlapping lesion of female genital organs |  |
| 1 |  | 1124 | Orta kulak | C30.1 | Orta qulaq | Middle ear |  |
| 1 |  | 1058 | Postkrikoid bölge | C13.0 | Üzükarxası nahiyə | Postcricoid region |  |
| 1 | ✅ | 1011 | Dilin dorsal yüzü, BBT | C02.0 | Dilin yuxarı səthi,ƏGO | Dorsal surface of tongue, NOS |  |

**Kapsam:** Bu 227 kod, canlı veride 2022-09'dan bugüne kullanılmış TÜM farklı değerleri
kapsıyor (`EMR.Pathology.EPulse`, ~52.000 kayıt). Teorik olarak SKRS'nin tanımlayabileceği
ama bu hastanede hiç kullanılmamış başka kodlar olabilir -- ileride böyle bir kod gelirse
mapper Skip etmeli, bu listeye eklenmeli.
