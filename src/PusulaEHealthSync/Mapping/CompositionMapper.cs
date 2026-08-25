using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Tedavi.GenelMuayene (epikriz) -> AZ Composition FHIR resource (profile: az-discharge-summary).
// Kaynak: sandbox $validate ile bos/minimal govdeler denenerek kesinlestirildi (2026-08-20,
// bkz. konusma) -- Patient/Encounter mapper'lariyla ayni yontem (docs/encounter-mapping.md).
//
// Zorunlu alanlar (sandbox $validate ile dogrulandi): status (1..1), type (1..1),
// subject (1..1), encounter (1..1), date (1..1), author (1..*), title (1..1),
// section (1..*) -- VE en az bir section.code, su LOINC kodlarindan biri olmali
// (az-ds-atleast-one-section invariant'i): 10154-3 (Sikayet), 11348-0 (Oyku),
// 29545-1 (Muayene bulgu), 11450-4 (Tani), 8648-8 (Tedavi/seyir), 8653-8 (Oneriler).
//
// Epikriz (RTF, tek serbest metin alani) HER ZAMAN "Tedavi/seyir" (8648-8) section'ina
// yazilir (invariant'i garanti eder). Sikayeti/Hikayesi+Soygecmisi/Bulgulari/Tani/
// TaburcuPlani -- bazen bos/sablon kalsa da (TedaviBakimPlani gibi) bazen DOLU oluyor
// (canli veride dogrulandi, 2026-08-21) -- doluysa EK, kendi AZ bolumune (Complaint/
// History/Examination/Diagnosis/Recommendations) yazilir.
public static class CompositionMapper
{
    private const string LoincSystem = "http://loinc.org";

    // KOK NEDEN (2026-08-21, kullanici bildirdi: "epikriz gönderiyoruz ama bakanlığın
    // ekranlarında göremiyorum"): Composition.type SADECE LOINC (18842-5) icerdiginden,
    // e-Health'in kendi belge sinifi CodeSystem'i (http://fhir.az/CodeSystem/composition-type
    // -- terminology API'den dogrulandi, 2026-08-21) hic doldurulmuyordu. AYNI kalip
    // EncounterMapper'daki hospital-departments/encounter-type sorunuyla ozdes: bakanlik
    // portali (vezandas ekranlari) belgeleri KENDI kodlariyla siniflandirip listeliyor,
    // yabanci (LOINC) bir sistemle gelen coding'i tanimiyor -- $validate hata vermiyor
    // (LOINC gecerli bir sistem) ama portalda hic gorunmuyor.
    //
    // DUZELTME (canli denemede bulundu): once bu AZ kodunu Composition.type.coding'e IKINCI
    // eleman olarak eklemistik -- 400 Bad Request ile geri geldi: az-discharge-summary
    // profili type.coding uzerinde SABIT PATTERN (system=loinc.org, code=18842-5,
    // display="Discharge summary") tasiyor, dizideki HER coding'in aynen bu degerlere
    // uymasini zorunlu kiliyor (server bunu slice-farkinda degil, dizinin TAMAMINA
    // uyguluyor). AZ kodu bu yuzden Composition'in AYRI bir alani olan "category"ye
    // (0..* -- type'tan bagimsiz, ek siniflandirma icin standart FHIR alani) tasindi.
    private const string AzCompositionTypeSystem = "http://fhir.az/CodeSystem/composition-type";

    // az-discharge-summary hem yatan (Stasionar) hem ayaktan (Ambulator) protokoller icin
    // kullanildigindan, AZ CodeSystem'deki iki karsilik gelen kod arasinda GelisTipiId'ye
    // gore secim yapiliyor -- EncounterMapper.Map'teki classCode (IMP/AMB) hesaplamasiyla
    // AYNI kural (p.GelisTipiId == "Y" -> yatan).
    private const string HospitalRecordCode = "hospital-record";
    private const string AmbulatoryRecordCode = "ambulatory";
    private const string ComplaintCode = "10154-3";
    private const string HistoryCode = "11348-0";
    private const string ExaminationCode = "29545-1";
    private const string DiagnosisCode = "11450-4";
    private const string TreatmentCode = "8648-8";
    private const string RecommendationsCode = "8653-8";

    // AZ tarafi terminology-api LOINC kodlarini barindirmiyor ($lookup denendi, 2026-08-21:
    // "Code not found") -- bu displayler AZ IG'nin KENDI $validate hata mesajindaki Ingilizce
    // adlandirmasindan alindi (Complaint/History/Examination/Diagnosis/Treatment/
    // Recommendations, bkz. yukaridaki yorum), resmi LOINC "long common name" degil.
    private static readonly Dictionary<string, string> SectionCodeDisplay = new()
    {
        [ComplaintCode] = "Chief complaint Narrative",
        [HistoryCode] = "History of past illness Narrative",
        [ExaminationCode] = "Physical findings Narrative",
        [DiagnosisCode] = "Problem list Reported",
        [TreatmentCode] = "Hospital course Narrative",
        [RecommendationsCode] = "Discharge instructions",
    };

    public static MappingResult Map(
        GenelMuayeneRecord m, ProtokolListItem p, string azPatientId, string azEncounterId, string azPractitionerId)
    {
        var epikrizPlain = RtfText.ToPlainText(m.Epikriz);
        // DUZELTME (2026-08-21, kullanici istegi): "kontrol et, hepsi ayrı ayrı kayıt
        // ediliyor" -- Sikayeti/Tani/TaburcuPlani disinda Hikayesi ve Bulgulari da AYRI
        // doluyor olabiliyor (canli ornekle dogrulandi), eskiden bunlar hic cekilmiyordu.
        // Artik hepsi kontrol ediliyor -- dolu olan HER biri kendi AZ bolumune ekleniyor,
        // Epikriz (Xəstəliyin gedişi) ise HER ZAMAN eklenen genel/butunlesik anlati.
        var historyText = string.Join("\n\n", new[] { RtfText.ToPlainText(m.Hikayesi), RtfText.ToPlainText(m.Soygecmisi) }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var hasStructuredContent = !string.IsNullOrWhiteSpace(m.Sikayeti)
            || !string.IsNullOrWhiteSpace(historyText)
            || !string.IsNullOrWhiteSpace(m.Bulgulari)
            || !string.IsNullOrWhiteSpace(m.Tani)
            || !string.IsNullOrWhiteSpace(m.TaburcuPlani);

        if (string.IsNullOrWhiteSpace(epikrizPlain) && !hasStructuredContent)
            return new MappingResult.Skipped("Epikriz metni boş -- gönderilecek içerik yok");

        var sections = new JsonArray();
        if (!string.IsNullOrWhiteSpace(epikrizPlain))
            sections.Add(BuildSection(TreatmentCode, "Xəstəliyin gedişi", epikrizPlain));
        if (!string.IsNullOrWhiteSpace(m.Sikayeti))
            sections.Add(BuildSection(ComplaintCode, "Şikayət", RtfText.ToPlainText(m.Sikayeti)));
        if (!string.IsNullOrWhiteSpace(historyText))
            sections.Add(BuildSection(HistoryCode, "Anamnez", historyText));
        if (!string.IsNullOrWhiteSpace(m.Bulgulari))
            sections.Add(BuildSection(ExaminationCode, "Müayinə Bulguları", RtfText.ToPlainText(m.Bulgulari)));
        if (!string.IsNullOrWhiteSpace(m.Tani))
            sections.Add(BuildSection(DiagnosisCode, "Diaqnoz", RtfText.ToPlainText(m.Tani)));
        if (!string.IsNullOrWhiteSpace(m.TaburcuPlani))
            sections.Add(BuildSection(RecommendationsCode, "Tövsiyələr", RtfText.ToPlainText(m.TaburcuPlani)));

        // Epikriz bos ama yapisal alanlardan biri doluysa (nadir), invariant'i saglamak
        // icin Tani/Sikayet zaten yukarida whitelisted kodlarla eklenmis oluyor -- ek islem
        // gerekmiyor. Hicbiri whitelisted kodlardan degilse (teorik olarak imkansiz, cunku
        // 4 kodun 3'u zaten whitelist'te) buraya dusmez.

        var compositionDate = ToAzInstant(m.EpikrizTamamlanmaTarihi ?? m.ModifiedDate ?? m.CreatedDate);
        var azCompositionTypeCode = p.GelisTipiId == "Y" ? HospitalRecordCode : AmbulatoryRecordCode;
        var azCompositionTypeDisplay = p.GelisTipiId == "Y" ? "Hospital Record" : "Ambulatory Care";

        var composition = new JsonObject
        {
            ["resourceType"] = "Composition",
            ["id"] = $"composition-{p.ProtokolId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-discharge-summary" } },
            ["status"] = "final",
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = "18842-5", ["display"] = "Discharge summary" },
                },
            },
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = AzCompositionTypeSystem, ["code"] = azCompositionTypeCode, ["display"] = azCompositionTypeDisplay },
                    },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" },
            ["date"] = compositionDate,
            ["author"] = new JsonArray { new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" } },
            ["title"] = "Epikriz",
            // KULLANICI ISTEGI (2026-08-21, "birşey atlamadığına emin misin?"): eksik oldugu
            // fark edilen bir alan -- $validate hata vermiyor (Composition.custodian 0..1,
            // zorunlu degil) ama EncounterMapper.serviceProvider ile AYNI Organization/5204
            // referansi burada da yoktu. Bir belgenin "sahibi/saklayicisi" kurum, portal
            // tarafinda dokuman-hasta iliskilendirmesi/goruntuleme icin kullanilan tipik bir
            // alan -- Encounter'daki bolum-adi sorununda oldugu gibi, $validate'in ZORUNLU
            // saymadigi ama portalin GORUNTULEME icin bekleyebilecegi bir alan olabilir.
            ["custodian"] = new JsonObject { ["reference"] = "Organization/5204", ["display"] = "Liv Bona Dea" },
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = p.ProtokolId.ToString(),
                },
            },
            ["section"] = sections,
        };

        return new MappingResult.Success(composition);
    }

    private static JsonObject BuildSection(string loincCode, string title, string plainText) => new()
    {
        ["title"] = title,
        ["code"] = new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject { ["system"] = LoincSystem, ["code"] = loincCode, ["display"] = SectionCodeDisplay[loincCode] },
            },
        },
        ["text"] = new JsonObject
        {
            ["status"] = "generated",
            ["div"] = RtfText.ToXhtmlDiv(plainText),
        },
    };

    // EncounterMapper.ToAzInstant ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";
}
