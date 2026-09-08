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

**KAPSAM ÖLÇÜMÜ (2026-09-08):** onaylı + neoplazili raporlarda fiilen geçen **158 farklı**
topografya kodunun TAMAMI bu tabloda mevcut -- canlı veride tek bir açık yok. (Tablodaki
227 girdinin geri kalanı, neoplazi olmayan raporlarda geçen kodlar.)

**MORFOLOJİ İÇİN BÖYLE BİR TABLOYA GEREK YOK:** `EPulse.MorfolojiKoduCode` zaten ICD-O-3
biçiminde (`8523/3`) ve AZ CodeSystem'i birebir aynı gösterimi kullanıyor -- doğrudan
geçiyor, çevrilmiyor.

Kod tabanına bağlanmadan önce: bu listedeki bir `SkrsKodu` karşılığı olmayan bir kayıtla
karşılaşılırsa mapper **atlamalı (Skip)**, asla tahmin etmemeli -- 400 karakter filtresindeki
aynı temkinli ilke.

| Sıklık | SkrsKodu | Pusula Terimi (TR) | ICD-O-3 Kodu | ICD-O-3 Terimi (EN) | Not |
|---:|---|---|---|---|---|
| 8867 | 1212 | Meme, BBT | C50.9 | Breast, NOS | |
| 5244 | 1222 | Serviks uteri | C53.9 | Cervix uteri, NOS | |
| 4143 | 1082 | Mide, BBT | C16.9 | Stomach, NOS | |
| 3987 | 1255 | Böbrek, BBT | C64.9 | Kidney, NOS | |
| 3175 | 1316 | Abdomen, BBT | C76.2 | Abdomen, NOS | |
| 2347 | 1143 | Akciğer, BBT | C34.9 | Lung, NOS | |
| 2329 | 1168 | Kemik iliği | C42.1 | Bone marrow | |
| 2258 | 1097 | Kolon, BBT | C18.9 | Colon, NOS | |
| 1816 | 1329 | Lenf düğümü, BBT | C77.9 | Lymph nodes, NOS | |
| 1645 | 1105 | Karaciğer | C22.0 | Liver | |
| 1288 | 1181 | Deri, BBT | C44.9 | Skin, NOS | |
| 930 | 1224 | Endometrium | C54.1 | Endometrium | |
| 921 | 1246 | Prostat bezi | C61.9 | Prostate gland | |
| 921 | 1229 | Uterus, BBT | C55.9 | Uterus, NOS | |
| 669 | 1291 | Beyin, BBT | C71.9 | Brain, NOS | |
| 669 | 1166 | Kemik, BBT | C41.9 | Bone, NOS | |
| 619 | 1220 | Ekzoserviks | C53.1 | Exocervix | |
| 425 | 1119 | Pankreas, BBT | C25.9 | Pancreas, NOS | |
| 415 | 1267 | Mesane, BBT | C67.9 | Bladder, NOS | |
| 381 | 1230 | Ovaryum | C56.9 | Ovary | |
| 369 | 1302 | Thyroid bezi | C73.9 | Thyroid gland | |
| 349 | 1314 | Baş, yüz ya da boyun, BBT | C76.0 | Head, face or neck, NOS | |
| 347 | 1199 | Bağ dokusu, subkutan doku ve diğer yumuşak dokular, BBT | C49.9 | Connective/soft tissue, NOS | |
| 335 | 1150 | Plevra, BBT | C38.4 | Pleura, NOS | |
| 329 | 1315 | Göğüs/Toraks | C76.1 | Thorax, NOS | |
| 329 | 1107 | Safra kesesi | C23.9 | Gallbladder | |
| 311 | 1083 | Duodenum | C17.0 | Duodenum | |
| 296 | 1073 | Özofagus, BBT | C15.9 | Esophagus, NOS | |
| 283 | 1219 | Endoserviks | C53.0 | Endocervix | |
| 274 | 1208 | Memenin üst-dış kadranı | C50.4 | Upper-outer quadrant of breast | |
| 265 | 1100 | Rektum, BBT | C20.9 | Rectum, NOS | |
| 256 | 1202 | Periton, BBT | C48.2 | Peritoneum, NOS | |
| 225 | 1305 | Adrenal bez, BBT | C74.9 | Adrenal gland, NOS | |
| 223 | 1095 | Sigmoid kolon | C18.7 | Sigmoid colon | |
| 221 | 1249 | Testis, BBT | C62.9 | Testis, NOS | |
| 204 | 1148 | Mediasten, BBT | C38.3 | Mediastinum, NOS | |
| 178 | 1076 | Mide korpusu | C16.2 | Body of stomach | |
| 168 | 1225 | Miyometrium | C54.2 | Myometrium | |
| 164 | 1191 | Baş, yüz ve boyun bağ dokusu... | C49.0 | Connective/soft tissue of head, face, neck | |
| 162 | 1099 | Appendiks | C18.1 | Appendix | |
| 141 | 1322 | Baş, yüz ve boyun lenf düğümleri | C77.0 | Lymph nodes of head, face, neck | |
| 141 | 1088 | İnce barsak, BBT | C17.9 | Small intestine, NOS | |
| 138 | 1217 | Vagina, BBT | C52.9 | Vagina | |
| 132 | 1195 | Abdomen bağ dokusu... | C49.4 | Connective/soft tissue of abdomen | |
| 130 | 1094 | Desendan kolon | C18.6 | Descending colon | |
| 118 | 1198 | Bağ dokusu...aşan lezyon | C49.8 | Overlapping lesion of connective/soft tissue | |
| 116 | 1090 | Asenden kolon | C18.2 | Ascending colon | |
| 110 | 1303 | Adrenal bez korteksi | C74.0 | Cortex of adrenal gland | |
| 106 | 1282 | Spinal meninksler | C70.1 | Spinal meninges | |
| 103 | 1138 | Ana bronşlar | C34.0 | Main bronchus | |
| 94 | 1317 | Pelvis, BBT | C76.3 | Pelvis, NOS | |
| 87 | 1077 | Gastrik antrum | C16.3 | Gastric antrum | |
| 87 | 1304 | Adrenal bez medullası | C74.1 | Medulla of adrenal gland | |
| 86 | 1068 | Abdominal özofagus | C15.2 | Abdominal esophagus | |
| 81 | 1325 | Aksilla ya da kol lenf düğümleri | C77.3 | Axillary lymph nodes / upper limb | |
| 79 | 1112 | Pankreas başı | C25.0 | Head of pancreas | |
| 77 | 1071 | Özofagusun 1/3 alt bölümü | C15.5 | Lower third of esophagus | |
| 77 | 1206 | Memenin üst-iç kadranı | C50.2 | Upper-inner quadrant of breast | |
| 76 | 1085 | İleum | C17.2 | Ileum | |
| 73 | 1186 | Abdomenin periferik ve otonom sinir sistemi | C47.4 | Peripheral/autonomic nerves of abdomen | |
| 68 | 1144 | Timus | C37.9 | Thymus | |
| 64 | 1319 | Alt ekstremite, BBT | C76.5 | Lower limb, NOS | |
| 64 | 1193 | Alt ekstremite ve kalça bağ dokusu... | C49.2 | Connective/soft tissue of lower limb, hip | |
| 61 | 1092 | Transvers kolon | C18.4 | Transverse colon | |
| 52 | 1209 | Memenin alt-dış kadranı | C50.5 | Lower-outer quadrant of breast | |
| 52 | 1136 | Larinks, BBT | C32.9 | Larynx, NOS | |
| 52 | 1164 | Pelvik kemik, sakrum, koksiks... | C41.4 | Pelvic bones, sacrum, coccyx | |
| 50 | 1075 | Mide fundusu | C16.1 | Fundus of stomach | |
| 49 | 1235 | Uterin adneks | C57.4 | Uterine adnexa | |
| 49 | 1170 | Dalak | C42.2 | Spleen | |
| 42 | 1034 | Ağız, BBT | C06.9 | Mouth, NOS | |
| 41 | 1211 | Memede aşan lezyon | C50.8 | Overlapping lesion of breast | |
| 41 | 1326 | İnguinal bölge ya da bacak lenf düğümleri | C77.4 | Inguinal lymph nodes / lower limb | |
| 40 | 1098 | Rektosigmoid bileşke | C19.9 | Rectosigmoid junction | |
| 40 | 1056 | Nasofarinks, BBT | C11.9 | Nasopharynx, NOS | |
| 38 | 1017 | Dil, BBT | C02.9 | Tongue, NOS | |
| 37 | 1156 | Alt ekstremite uzun kemikleri... | C40.2 | Long bones of lower limb | |
| 35 | 1162 | Vertebral kolon/Omurga | C41.2 | Vertebral column | |
| 33 | 1205 | Memenin santral bölgesi | C50.1 | Central portion of breast | |
| 30 | 1179 | Alt ekstremite ve kalça derisi | C44.7 | Skin of lower limb, hip | |
| 30 | 1102 | Anal kanal | C21.1 | Anal canal | |
| 27 | 1113 | Pankreas gövdesi | C25.1 | Body of pancreas | |
| 27 | 1157 | Alt ekstremite kısa kemikleri... | C40.3 | Short bones of lower limb | |
| 26 | 1177 | Gövde derisi | C44.5 | Skin of trunk | |
| 25 | 1079 | Mide küçük kurvatur, BBT | C16.5 | Lesser curvature of stomach, NOS | |
| 24 | 1318 | Üst ekstremite, BBT | C76.4 | Upper limb, NOS | |
| 24 | 1145 | Kalp | C38.0 | Heart | |
| 23 | 1089 | Çekum | C18.0 | Cecum | |
| 22 | 1070 | Özofagusun 1/3 orta bölümü | C15.4 | Middle third of esophagus | |
| 21 | 1080 | Mide büyük kurvatur, BBT | C16.6 | Greater curvature of stomach, NOS | |
| 21 | 1154 | Üst ekstremite uzun kemikleri, skapula... | C40.0 | Long bones of upper limb, scapula | |
| 21 | 1069 | Özofagusun 1/3 üst bölümü | C15.3 | Upper third of esophagus | |
| 21 | 1192 | Üst ekstremite ve omuz bağ dokusu... | C49.1 | Connective/soft tissue of upper limb, shoulder | |
| 20 | 1109 | Vater ampullası | C24.1 | Ampulla of Vater | |
| 20 | 1200 | Retroperitoneum | C48.0 | Retroperitoneum | |
| 20 | 1197 | Gövde bağ dokusu... BBT | C49.6 | Connective/soft tissue of trunk, NOS | |
| 19 | 1104 | Rektum, anus ve anal kanalda aşan lezyon | C21.8 | Overlapping lesion of rectum/anus/anal canal | |
| 19 | 1257 | Üreter | C66.9 | Ureter | |
| 19 | 1228 | Korpus uteri | C54.9 | Corpus uteri, NOS | |
| 18 | 1204 | Memebaşı | C50.0 | Nipple | |
| 16 | 1074 | Kardia, BBT | C16.0 | Cardia, NOS | |
| 16 | 1043 | Tonsil, BBT | C09.9 | Tonsil, NOS | |
| 16 | 1084 | Jejunum | C17.1 | Jejunum | |
| 16 | 1218 | Vulva, BBT | C51.9 | Vulva, NOS | |
| 15 | 1111 | Safra yolları, BBT | C24.9 | Biliary tract, NOS | |
| 14 | 1280 | Göz, BBT | C69.9 | Eye, NOS | |
| 14 | 1324 | İntra-abdominal lenf düğümleri | C77.2 | Intra-abdominal lymph nodes | |
| 14 | 1207 | Memenin alt-iç kadranı | C50.3 | Lower-inner quadrant of breast | |
| 13 | 1203 | Retroperiton ve peritonda aşan lezyon | C48.8 | Overlapping lesion of retroperitoneum/peritoneum | |
| 13 | 1035 | Parotis bezi | C07.9 | Parotid gland | |
| 12 | 1165 | Kemik, eklem ve eklem kıkırdaklarında aşan lezyon | C41.8 | Overlapping lesion of bone/joint/cartilage | ⚠ |
| 12 | 1210 | Memenin aksiller kuyruğu | C50.6 | Axillary tail of breast | |
| 11 | 1176 | Baş saçlı deri ve boyun derisi | C44.4 | Skin of scalp and neck | |
| 11 | 1226 | Fundus uteri | C54.3 | Fundus uteri | |
| 11 | 1142 | Akciğerde aşan lezyon | C34.8 | Overlapping lesion of lung | |
| 11 | 1101 | Anus, BBT | C21.0 | Anus, NOS | |
| 11 | 1091 | Kolonun hepatik fleksuru | C18.3 | Hepatic flexure of colon | |
| 11 | 1194 | Toraks bağ dokusu... | C49.3 | Connective/soft tissue of thorax | |
| 10 | 1155 | Üst ekstremite kısa kemikleri... | C40.1 | Short bones of upper limb | |
| 10 | 1182 | Baş, yüz, boyun periferik ve otonom sinir sistemi | C47.0 | Peripheral/autonomic nerves of head, face, neck | |
| 10 | 1293 | Kauda ekuina | C72.1 | Cauda equina | |
| 10 | 1307 | Hipofiz bezi | C75.1 | Pituitary gland | |
| 10 | 1009 | Dudak, BBT | C00.9 | Lip, NOS | |
| 9 | 1114 | Pankreas kuyruğu | C25.2 | Tail of pancreas | |
| 9 | 1052 | Nasofarinks posteriyör duvarı | C11.1 | Posterior wall of nasopharynx | |
| 9 | 1163 | Kosta, sternum, klavikula... | C41.3 | Rib, sternum, clavicle | |
| 9 | 1078 | Pilor | C16.4 | Pylorus | |
| 8 | 1284 | Serebrum/beyin | C71.0 | Cerebrum | |
| 8 | 1174 | Dış kulak | C44.2 | Skin of ear and external auricular canal | |
| 8 | 1173 | Göz kapağı | C44.1 | Skin of eyelid, including canthus | |
| 8 | 1260 | Mesane yan duvarı | C67.2 | Lateral wall of bladder | |
| 8 | 1201 | Peritonun spesifik bölgeleri | C48.1 | Specified parts of peritoneum | ⚠ |
| 7 | 1031 | Ağız vestibülü | C06.1 | Vestibule of mouth | |
| 7 | 1256 | Renal pelvis | C65.9 | Renal pelvis | |
| 7 | 1024 | Ağız tabanı, BBT | C04.9 | Floor of mouth, NOS | |
| 7 | 1320 | Diğer iyi tanımlanmamış bölgeler | C76.7 | Other ill-defined sites | |
| 7 | 1239 | Plasenta | C58.9 | Placenta | |
| 7 | 1081 | Midede aşan lezyon | C16.8 | Overlapping lesion of stomach | |
| 6 | 1221 | Serviks uteride aşan lezyon | C53.8 | Overlapping lesion of cervix uteri | |
| 6 | 1093 | Kolonun splenik fleksuru | C18.5 | Splenic flexure of colon | |
| 6 | 1178 | Üst ekstremite ve omuz derisi | C44.6 | Skin of upper limb and shoulder | |
| 6 | 1019 | Alt dişeti | C03.1 | Lower gum | |
| 6 | 1122 | Gastrointestinal kanal, BBT | C26.9 | Gastrointestinal tract, NOS | |
| 6 | 1306 | Parathyroid bezi | C75.0 | Parathyroid gland | |
| 5 | 1141 | Alt lob, akciğer | C34.3 | Lower lobe, lung | |
| 5 | 1327 | Pelvik lenf düğümü | C77.5 | Pelvic lymph nodes | |
| 5 | 1216 | Vulvayı aşan lezyonlar | C51.8 | Overlapping lesion of vulva | |
| 5 | 1036 | Submandibuler bez | C08.0 | Submandibular gland | |
| 4 | 1245 | SKROTUMm, BBT | C63.2 | Scrotum, NOS | |
| 4 | 1283 | Meninksler, BBT | C70.9 | Meninges, NOS | |
| 4 | 1263 | Mesane boynu | C67.5 | Bladder neck | |
| 4 | 1286 | Temporal lob | C71.2 | Temporal lobe | |
| 4 | 1066 | Servikal özofagus | C15.0 | Cervical esophagus | |
| 4 | 1175 | Yüzün diğer ve spesifiye edilmeyen bölgelerinin derisi | C44.3 | Skin of other/unspecified face parts | |
| 3 | 1146 | Anteriyör mediasten | C38.1 | Anterior mediastinum | |
| 3 | 1130 | Aksesuvar sinus, BBT | C31.9 | Accessory sinus, NOS | |
| 3 | 1312 | Endokrin bezleri ve ilgili yapılarda aşan lezyon | C75.8 | Overlapping lesion of endocrine glands | |
| 3 | 1236 | Kadın genital organların diğer spesifik bölgeleri | C57.7 | Other specified parts of female genital organs | ⚠ |
| 3 | 1276 | Orbita, BBT | C69.6 | Orbit, NOS | |
| 3 | 1180 | Deride aşan lezyonlar | C44.8 | Overlapping lesion of skin | |
| 3 | 1261 | Mesane anteriyor duvarı | C67.3 | Anterior wall of bladder | |
| 3 | 1149 | Kalp, mediasten ve plevrada aşan lezyon | C38.8 | Overlapping lesion of heart/mediastinum/pleura | |
| 3 | 1300 | Beyin ve merkez sinir sisteminde aşan lezyon | C72.8 | Overlapping lesion of brain/CNS | |
| 3 | 1106 | İntrehepatik safra yolları | C22.1 | Intrahepatic bile duct | |
| 3 | 1134 | Laringeal kıkırdak | C32.3 | Laryngeal cartilage | |
| 2 | 1268 | Üretra | C68.0 | Urethra | |
| 2 | 1049 | Orofarinksde aşan lezyon | C10.8 | Overlapping lesion of oropharynx | |
| 2 | 1328 | Multipl bölgelerin lenf düğümleri | C77.8 | Lymph nodes of multiple regions | |
| 2 | 1262 | Mesane posteriyor duvarı | C67.4 | Posterior wall of bladder | |
| 2 | 1292 | Spinal Kord | C72.0 | Spinal cord | |
| 2 | 1016 | Dilde aşan lezyon | C02.8 | Overlapping lesion of tongue | |
| 2 | 1126 | Ethmoid sinüs | C31.1 | Ethmoid sinus | |
| 2 | 1289 | Beyin sapı | C71.7 | Brain stem | |
| 2 | 1271 | Üriner sistem, BBT | C68.9 | Urinary system, NOS | |
| 2 | 1087 | İnce barsağı aşan lezyonlar | C17.8 | Overlapping lesion of small intestine | |
| 2 | 1050 | Orofarinks, BBT | C10.9 | Oropharynx, NOS | |
| 2 | 1139 | Üst lob, akciğer | C34.1 | Upper lobe, lung | |
| 2 | 1063 | Farinks, BBT | C14.0 | Pharynx, NOS | |
| 2 | 1123 | Nasal kavite | C30.0 | Nasal cavity | |
| 2 | 1244 | Penis, BBT | C60.9 | Penis, NOS | |
| 2 | 1030 | Yanak mukozası | C06.0 | Cheek mucosa | |
| 2 | 1231 | Fallop tüpü | C57.0 | Fallopian tube | |
| 1 | 1108 | Ekstrahepatik safra yolları | C24.0 | Extrahepatic bile duct | |
| 1 | 1014 | Dilin 2/3 anteriyoru, BBT | C02.3 | Anterior two-thirds of tongue, NOS | |
| 1 | 1214 | Labium minus | C51.1 | Labium minus | |
| 1 | 1321 | İyi tanımlanmamış bölgede aşan lezyon | C76.8 | Overlapping lesion of ill-defined sites | |
| 1 | 1028 | Damakta aşan lezyon | C05.8 | Overlapping lesion of palate | |
| 1 | 1140 | Orta lob, akciğer | C34.2 | Middle lobe, lung | |
| 1 | 1110 | Safra yollarında aşan lezyon | C24.8 | Overlapping lesion of biliary tract | |
| 1 | 1010 | Dil tabanı, BBT | C01.9 | Base of tongue | |
| 1 | 1252 | Diğer spesifiye edilebilen erkek genital organları | C63.7 | Other specified parts of male genital organs | ⚠ |
| 1 | 1227 | Korpus uteride aşan lezyon | C54.8 | Overlapping lesion of corpus uteri | |
| 1 | 1161 | Mandibula | C41.1 | Mandible | |
| 1 | 1055 | Nasofarinksde aşan lezyon | C11.8 | Overlapping lesion of nasopharynx | |
| 1 | 1313 | Endokrin bez, BBT | C75.9 | Endocrine gland, NOS | |
| 1 | 1039 | Büyük tükrük bezleri, BBT | C08.9 | Major salivary gland, NOS | |
| 1 | 1067 | Torasik özofagus | C15.1 | Thoracic esophagus | |
| 1 | 1062 | Hipofarinks, BBT | C13.9 | Hypopharynx, NOS | |
| 1 | 1012 | Dilin sınırları | C02.1 | Border of tongue | |
| 1 | 1215 | Klitoris | C51.0 | Clitoris | |
| 1 | 1120 | Intestinal trakt, BBT | C26.0 | Intestinal tract, NOS | ⚠ |
| 1 | 1269 | Paraürertal bez | C68.1 | Paraurethral gland | |
| 1 | 1005 | Alt dudak mukozası | C00.4 | Lower lip, inner aspect | |
| 1 | 1026 | Yumuşak damak, BBT | C05.1 | Soft palate, NOS | |
| 1 | 1290 | Beyinde aşan lezyon | C71.8 | Overlapping lesion of brain | |
| 1 | 1042 | Tonsilde aşan lezyon | C09.8 | Overlapping lesion of tonsil | |
| 1 | 1003 | Dış dudak, BBT | C00.2 | External lip, NOS | |
| 1 | 1048 | Branchial yarık | C10.4 | Branchial cleft | |
| 1 | 1045 | Epiglot anterior yüzey | C10.1 | Anterior surface of epiglottis | |
| 1 | 1033 | Ağzın diğer ve spesifiye edilemeyen bölgelerinde aşan lezyon | C06.8 | Overlapping lesion of mouth | |
| 1 | 1147 | Posteriyor mediasten | C38.2 | Posterior mediastinum | |
| 1 | 1301 | Sinir sistemi, BBT | C72.9 | Nervous system, NOS | |
| 1 | 1020 | Dişeti, BBT | C03.9 | Gum, NOS | |
| 1 | 1117 | Pankreasın diğer spesifiye edilebilen bölgeleri | C25.7 | Other specified parts of pancreas | |
| 1 | 1158 | Ekstremite kemikleri, eklemler ve eklem kıkırdaklarında aşan lezyon | C40.8 | Overlapping lesion of limb bones/joints/cartilage | ⚠ |
| 1 | 1323 | İntratorasik lenf düğümleri | C77.1 | Intrathoracic lymph nodes | |
| 1 | 1037 | Sublingual bez | C08.1 | Sublingual gland | |
| 1 | 1275 | Lakrimal bez | C69.5 | Lacrimal gland | |
| 1 | 1029 | Damak/Palate, BBT | C05.9 | Palate, NOS | |
| 1 | 1196 | Pelvis bağ dokusu, subkutan doku ve diğer yumuşak dokular | C49.5 | Connective/soft tissue of pelvis | |
| 1 | 1213 | Labium majus | C51.2 | Labium majus | |
| 1 | 1007 | Dudak komissür | C00.6 | Commissure of lip | |
| 1 | 1001 | Üst dış dudak | C00.0 | External upper lip | |
| 1 | 1237 | Kadın genital organlarında aşan lezyon | C57.8 | Overlapping lesion of female genital organs | |
| 1 | 1124 | Orta kulak | C30.1 | Middle ear | |
| 1 | 1058 | Postkrikoid bölge | C13.0 | Postcricoid region | |
| 1 | 1011 | Dilin dorsal yüzü, BBT | C02.0 | Dorsal surface of tongue, NOS | |

**Kapsam:** Bu 227 kod, canlı veride 2022-09'dan bugüne kullanılmış TÜM farklı değerleri
kapsıyor (`EMR.Pathology.EPulse`, ~52.000 kayıt). Teorik olarak SKRS'nin tanımlayabileceği
ama bu hastanede hiç kullanılmamış başka kodlar olabilir -- ileride böyle bir kod gelirse
mapper Skip etmeli, bu listeye eklenmeli.
