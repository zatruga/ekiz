using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// [EMR.Pathology].[EPulse] satiri (PathologyFindingRecord) -> AZ Observation FHIR resource
// (profile: az-pathology-finding). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-pathology-finding.json (IG'den okundu, 2026-09-08).
//
// Zorunlu alanlar: extension:local-system-unique-id (1..1), category (1..1, SABIT
// observation-category#laboratory -- DiagnosticReport'taki v2-0074#PAT ile KARISTIRMA,
// bunlar farkli iki CodeSystem), code (1..1, SABIT LOINC 59847-4 "Histology and Behavior
// ICD-O-3"), effective[x]:effectiveDateTime (1..1), component (2..*).
//
// component SLICE'lari -- ikisi de 1..1 ZORUNLU ve ikisi de REQUIRED binding:
//   morphology -> LOINC 59848-2, deger VS: icd-o-3-morphology-vs
//   topography -> LOINC 21855-2, deger VS: icd-o-3-topography-vs
// Iki VS de AYNI CodeSystem'i (http://fhir.az/CodeSystem/az-icd-o-3) suzuyor, sadece regex
// farkli: morfoloji "^[8-9].*", topografya "^C.*". Yani iki ekseni ayiran sey LOINC kodu.
//
// BICIMLER IG'den BIREBIR DOGRULANDI (2026-09-08): morfoloji "8500/3" (bolu isaretli),
// topografya "C50.9" (NOKTALI). Pusula'nin MorfolojiKoduCode alani zaten TAM bu bicimde --
// cevirim yok. Topografya ise SKRS ic kodu, bkz. PathologyTopographyMap.
//
// DISPLAY BILEREK YAZILMIYOR: AZ CodeSystem'indeki display'ler Azerice ("C50.9" =
// "Sud vezi, EGO") ve elimizde tam liste yok; yanlis bir display gondermek yerine
// CodeableConcept.text'e Pusula'nin kendi Turkce terimi yaziliyor -- text zaten tam olarak
// "kaynaktaki insan tarafindan okunabilir karsilik" icin var.
//
// ACIK SORU (canli $validate ile netlesecek): az-lab-result-observation'da
// extension:procedure-code 1..1 ZORUNLU cikmisti (bkz. LabResultObservationMapper -- IG
// differential'inda gorunmuyordu, sunucu $validate'te "Instance count ... is 0" diye
// reddetti). az-pathology-finding'in IG differential'i bunu ISTEMIYOR. Ayni AZObservation
// tabanindan turedigi icin sunucunun yine isteme IHTIMALI var. Tahmin edilmedi: once IG'nin
// yazdigi gibi (procedure-code'suz) uretiliyor, $validate aksini soylerse eklenecek --
// Icbari kodu cagiran tarafta (PathologyReportRecord.IcbariKodu) hazir duruyor.
public static class PathologyFindingMapper
{
    private const string LoincSystem = "http://loinc.org";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string IcdO3System = "http://fhir.az/CodeSystem/az-icd-o-3";
    private const string LocalUniqueIdExtensionUrl = "http://fhir.az/StructureDefinition/local-system-unique-id";

    private const string PathologyFindingLoincCode = "59847-4";
    private const string MorphologyLoincCode = "59848-2";
    private const string TopographyLoincCode = "21855-2";

    // ICD-O-3 morfoloji: 4 hane (8000-9999) + "/" + tek haneli davranis kodu. Canli veride
    // 44 satir bunu ihlal ediyor ("8130/21", "8130/23") -- ICD-O-3'un 6. hanesi olan
    // derece/grade eki yapismis gorunuyor. TAHMIN EDILIP kirpilmiyor ("8130/21" -> "8130/2"
    // makul gorunse de bu bir varsayim olurdu); required binding'li bir VS'e uydurma kod
    // gondermek yerine bu satirlar Skipped olup gorunur oluyor.
    //
    // DIKKAT -- burada SADECE BICIM denetleniyor, uyelik DEGIL. Olculdu (2026-09-09, AZ
    // CodeSystem'inin tam JSON'u indirilip karsilastirildi): hastanede kullanilan 359
    // morfoloji kodunun 18'i az-icd-o-3 (surum 0.1.1) listesinde YOK -- 104 satir / 60
    // rapor. Bunlar uydurma degil, WHO'nun daha yeni surumlerinde (ICD-O-3.2) tanimli
    // gercek kodlar (orn. 8509/3 invaziv solid papiller karsinom, 8380/2 EIN, 8507/3
    // invaziv mikropapiller); AZ listesi daha eski bir alt kume.
    //
    // KULLANICI KARARI (2026-09-09): bu kodlar YINE DE GONDERILIYOR. Gerekce: kodlar
    // gecerli ICD-O-3, sunucu kabul ediyor (required binding zaten denetlenmiyor -- bkz.
    // sinif yorumundaki $validate notu) ve alternatif, 60 gercek kanser bulgusunu hic
    // gondermemek olurdu. Bilincli olarak uyelik denetimi EKLENMEDI: AZ'nin 1137 morfoloji
    // kodunu repoya gomup her yeni WHO kodunu elle bakim etmek, cozdugunden cok sorun
    // uretirdi. Bakanlik listeyi guncellerse fark kendiliginden kapanir --
    // docs/bakanlik-sorulari.md'de acik soru olarak, tam 18'lik listesiyle duruyor.
    //
    // TOPOGRAFYA tarafinda boyle bir acik YOK: PathologyTopographyMap'teki 227 ICD-O-3
    // kodunun TAMAMI az-icd-o-3'te mevcut (ayni gun, ayni yontemle dogrulandi).
    private static readonly Regex IcdO3MorphologyPattern = new(@"^[89]\d{3}/\d$", RegexOptions.Compiled);

    public static MappingResult Map(PathologyFindingRecord finding, string azPatientId, string? azEncounterId, string? azPractitionerId)
    {
        var morphology = finding.MorfolojiKoduCode?.Trim();
        if (string.IsNullOrWhiteSpace(morphology) || !IcdO3MorphologyPattern.IsMatch(morphology))
            return new MappingResult.Skipped($"Morfoloji kodu ICD-O-3 biçiminde değil (\"{finding.MorfolojiKoduCode}\") -- component:morphology zorunlu alanı required binding'li bir değer kümesine bağlı, uydurma kod gönderilemez");

        if (!PathologyTopographyMap.TryGetIcdO3(finding.YerlesimYeriCode, out var topography))
            return new MappingResult.Skipped($"Yerleşim yeri kodu (\"{finding.YerlesimYeriCode}\" -- {finding.YerlesimYeriValue}) ICD-O-3 topografya karşılığı tablosunda yok -- component:topography zorunlu alanı doldurulamıyor. Eşleşme docs/patoloji-topografya-icdo3-eslestirme.md dosyasına eklenmeli.");

        var observation = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = $"observation-patoloji-bulgu-{finding.IslemReferansNumarasi}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-pathology-finding" } },
            ["status"] = "final",
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = CategorySystem, ["code"] = "laboratory", ["display"] = "Laboratory" },
                    },
                },
            },
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = PathologyFindingLoincCode, ["display"] = "Histology and Behavior ICD-O-3" },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["effectiveDateTime"] = ToAzInstant(finding.RaporlamaZamani ?? finding.IstemZamani),
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = LocalUniqueIdExtensionUrl,
                    ["valueString"] = LocalUniqueId(finding.IslemReferansNumarasi),
                },
            },
            ["component"] = new JsonArray
            {
                Component(MorphologyLoincCode, "Morphology.ICD-O-3", "Morfologiya (XBT-O-3)", morphology, finding.MorfolojiKoduValue),
                Component(TopographyLoincCode, "Primary site Cancer", "Topoqrafiya (XBT-O-3)", topography, finding.YerlesimYeriValue),
            },
        };

        if (!string.IsNullOrWhiteSpace(azEncounterId))
            observation["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        if (!string.IsNullOrWhiteSpace(azPractitionerId))
            observation["performer"] = new JsonArray { new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" } };

        return new MappingResult.Success(observation);
    }

    private static JsonObject Component(string loincCode, string loincDisplay, string codeText, string icdO3Code, string? sourceTerm)
    {
        var value = new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject { ["system"] = IcdO3System, ["code"] = icdO3Code },
            },
        };

        // Pusula'nin kendi Turkce terimi -- AZ display'i yerine text'e. Bkz. sinif yorumu.
        if (!string.IsNullOrWhiteSpace(sourceTerm))
            value["text"] = sourceTerm.Trim();

        return new JsonObject
        {
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = loincCode, ["display"] = loincDisplay },
                },
                ["text"] = codeText,
            },
            ["valueCodeableConcept"] = value,
        };
    }

    // PathologyReportMapper/RadiologyReportMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";

    // "patoloji-bulgu-" oneki: Observation'a Laboratuvar sonuclari da gidiyor
    // (LabResultObservationMapper) ve iki ID uzayi BAGIMSIZ/ORTUSEN -- onek olmadan
    // FindExistingIdAsync yanlis kaydi bulup uzerine yazabilirdi. Ayni gerekce:
    // PathologyReportMapper.LocalUniqueId.
    public static string LocalUniqueId(int islemReferansNumarasi) => $"patoloji-bulgu-{islemReferansNumarasi}";
}
