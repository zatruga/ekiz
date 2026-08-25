using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Hasta.ProtokolIslem -> AZ Procedure FHIR resource (profile: az-procedure).
// Kaynak: https://fhir.e-health.gov.az/StructureDefinition-az-procedure.json differential'i
// dogrudan indirilip okunarak kesinlestirildi (2026-08-25).
//
// Zorunlu alanlar: local-system-unique-id extension (1..1), code (1..1, az-procedure-codes-vs
// required binding -- coding.system/code/display'in UCU de zorunlu), subject.reference (1..1),
// encounter.reference (1..1, Encounter'in GERCEK AZ id'si once bilinmeli -- ConditionMapper
// ile ayni iki-asamali kalip). performed[x] opsiyonel ama IslemTarihi doluysa gonderiliyor.
public static class ProcedureMapper
{
    private const string ProcedureCodeSystem = "http://fhir.az/CodeSystem/az-procedure-codes";

    public static MappingResult Map(IslemRecord islem, string azPatientId, string azEncounterId)
    {
        var kod = islem.IcbariKodu.TrimEnd('.');

        var procedure = new JsonObject
        {
            ["resourceType"] = "Procedure",
            ["id"] = $"procedure-{islem.Id}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-procedure" } },
            ["status"] = "completed",
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = ProcedureCodeSystem, ["code"] = kod, ["display"] = islem.IcbariAdi },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" },
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = islem.Id.ToString(),
                },
            },
        };

        if (islem.IslemTarihi is not null)
            procedure["performedDateTime"] = ToAzInstant(islem.IslemTarihi.Value);

        return new MappingResult.Success(procedure);
    }

    // EncounterMapper/ConditionMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";
}
