using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// [EMR.Pathology].[Result] + bulgulari -> AZ Composition FHIR resource (profile:
// az-pathology-report-composition). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-pathology-report-composition.json (IG'den okundu, 2026-09-08).
//
// Zorunlu alanlar: extension:local-system-unique-id (1..1), status (1..1), type (1..1,
// SABIT LOINC 11526-1 "Pathology study" -- DiagnosticReport.code ile AYNI kod), subject
// (1..1, az-patient), date (1..1), author (1..*, az-practitioner VEYA Organization),
// title (1..1). encounter 0..1, section.entry -> az-pathology-finding.
//
// GONDERIM SIRASI (bu sinifin neden Observation ID'lerini disaridan aldigi): zincir
//   Observation'lar -> Composition (onlara referans verir) -> DiagnosticReport
//   (extension:composition ile Composition'a referans verir)
// seklinde TEK YONLU. Bu yuzden once bulgular gonderilir, donen AZ id'leri buraya
// verilir, en son DiagnosticReport gonderilir. Bu sira sayesinde DiagnosticReport'u iki
// kez yazmak (once ekstensiyonsuz, sonra guncelleyerek) GEREKMIYOR.
//
// type.coding'e IKINCI bir coding EKLENMEMELI: az-discharge-summary'de bunu denerken
// 400 Bad Request alinmisti -- sunucu SABIT PATTERN'i dizinin TAMAMINA uyguluyor, slice
// farkinda degil (tam gerekce: CompositionMapper). Ayni risk burada da gecerli oldugu icin
// type tek coding ile birakildi.
public static class PathologyCompositionMapper
{
    private const string LoincSystem = "http://loinc.org";
    private const string PathologyStudyLoincCode = "11526-1";
    private const string LocalUniqueIdExtensionUrl = "http://fhir.az/StructureDefinition/local-system-unique-id";

    public static MappingResult Map(
        PathologyReportRecord report,
        IReadOnlyList<string> azFindingIds,
        string azPatientId,
        string? azEncounterId,
        string? azPractitionerId)
    {
        // Composition'in TEK varlik sebebi bulgulari tasimak -- bulgu yoksa gonderilmez.
        // Bu, neoplazi olmayan raporlarin (canli veride %75) normal yolu: zincir hic
        // kurulmaz, rapor v1'deki gibi sadece DiagnosticReport olarak gider. composition
        // extension'i 0..1 oldugu icin bu profile UYGUN bir sonuc, eksiklik degil.
        if (azFindingIds.Count == 0)
            return new MappingResult.Skipped("Kodlanmış patoloji bulgusu yok -- Composition/Observation zinciri kurulmuyor, rapor yalnızca DiagnosticReport olarak gönderiliyor");

        // author 1..* ZORUNLU. Raporu onaylayan hekim e-Health'e gonderilmemisse
        // Organization'a dusuluyor -- profil ikisine de izin veriyor, bu yuzden eksik
        // hekim yuzunden butun zinciri atlamak gereksiz olurdu.
        var author = string.IsNullOrWhiteSpace(azPractitionerId)
            ? new JsonObject { ["reference"] = "Organization/5204", ["display"] = "Liv Bona Dea" }
            : new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" };

        var entries = new JsonArray();
        foreach (var id in azFindingIds)
            entries.Add(new JsonObject { ["reference"] = $"Observation/{id}" });

        var composition = new JsonObject
        {
            ["resourceType"] = "Composition",
            ["id"] = $"composition-patoloji-{report.ResultId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-pathology-report-composition" } },
            ["status"] = "final",
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = PathologyStudyLoincCode, ["display"] = "Pathology study" },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["date"] = ToAzInstant(report.ApprovedDate ?? report.RequestedAt ?? DateTime.Now),
            ["author"] = new JsonArray { author },
            ["title"] = "Patoloji hesabatı",
            // Epikriz Composition'daki ayni gerekce: $validate zorunlu saymiyor ama portal
            // dokuman-kurum iliskilendirmesi icin bekleyebiliyor (bkz. CompositionMapper).
            ["custodian"] = new JsonObject { ["reference"] = "Organization/5204", ["display"] = "Liv Bona Dea" },
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = LocalUniqueIdExtensionUrl,
                    ["valueString"] = LocalUniqueId(report.ResultId),
                },
            },
            ["section"] = new JsonArray
            {
                // section.code BILEREK yazilmiyor: profil section icin zorunlu bir kod/slice
                // tanimlamiyor (az-discharge-summary'deki "en az bir section.code sunlardan
                // biri olmali" invariant'inin karsiligi burada YOK) ve uydurma bir LOINC
                // kodu gondermek reddedilme riski. section 0..1 code ile gecerli; icerik
                // zaten entry'lerde (FHIR cmp-1: text, entry veya alt-section'dan biri yeter).
                new JsonObject
                {
                    ["title"] = "Patoloji tapıntıları",
                    ["entry"] = entries,
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(azEncounterId))
            composition["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        return new MappingResult.Success(composition);
    }

    // PathologyReportMapper/RadiologyReportMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";

    // "patoloji-" oneki SART: Epikriz de Composition olarak gidiyor ve orada
    // local-system-unique-id CIPLAK ProtokolId (bkz. CompositionMapper) -- onek olmadan
    // ResultId ile ProtokolId ayni sayiya denk gelip FindExistingIdAsync'in bir epikrizi
    // patoloji raporuyla ezmesine yol acabilirdi.
    public static string LocalUniqueId(int resultId) => $"patoloji-{resultId}";
}
