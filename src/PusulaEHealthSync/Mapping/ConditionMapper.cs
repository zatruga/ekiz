using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Tedavi.ProtokolICD -> AZ Condition FHIR resource (profile: az-condition).
// Kaynak: https://fhir.e-health.gov.az/StructureDefinition-az-condition.json (KULLANICI
// ISTEGI, 2026-08-24: "artik ana kilavuzumuz bu site olacak" -- IG'nin differential'i
// dogrudan indirilip okunarak kesinlestirildi, $validate deneme-yanilma DEGIL).
//
// Zorunlu alanlar (StructureDefinition differential'inden): local-system-unique-id
// extension (1..1), verificationStatus (standart HL7 condition-ver-status, required
// binding), category (standart HL7 condition-category, required binding), code (1..1,
// ICD-10, http://fhir.az/ValueSet/icd-10-vs required binding -- code+display ikisi de
// zorunlu), subject, encounter.reference (1..1), recordedDate (1..1).
public static class ConditionMapper
{
    private const string IcdSystem = "http://fhir.az/CodeSystem/az-icd-10";
    private const string VerificationStatusSystem = "http://terminology.hl7.org/CodeSystem/condition-ver-status";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/condition-category";

    public static MappingResult Map(IcdTaniRecord tani, ProtokolListItem p, string azPatientId, string azEncounterId)
    {
        var condition = new JsonObject
        {
            ["resourceType"] = "Condition",
            ["id"] = $"condition-{p.ProtokolId}-{tani.ICDId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-condition" } },
            ["verificationStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = VerificationStatusSystem, ["code"] = "confirmed", ["display"] = "Confirmed" },
                },
            },
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = CategorySystem, ["code"] = "encounter-diagnosis", ["display"] = "Encounter Diagnosis" },
                    },
                },
            },
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = IcdSystem, ["code"] = tani.Kodu, ["display"] = tani.Adi ?? tani.Kodu },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" },
            ["recordedDate"] = ToAzInstant(p.AcilisTarihi ?? DateTime.Now),
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = $"{p.ProtokolId}-{tani.ICDId}",
                },
            },
        };

        return new MappingResult.Success(condition);
    }

    // EncounterMapper/CompositionMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";
}
