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

    public static MappingResult Map(
        IslemRecord islem, string azPatientId, string azEncounterId,
        IReadOnlyDictionary<int, (string? Kod, bool Gonderilmez)>? eslestirme = null)
    {
        // HIZMET ESLESTIRME EKRANININ KARARI (HizmetMappingStore, 2026-09-24).
        // Iki ayri sey soyleyebiliyor:
        //   Gonderilmez -> bu kalem bakanliga hic gitmez (yatak ucreti, recete islemi,
        //                  CD ucreti gibi TIBBI ISLEM OLMAYAN kalemler)
        //   Kod         -> Icbari kodu yoksa elle girilen bakanlik kodu
        var karar = eslestirme is not null && eslestirme.TryGetValue(islem.HizmetId, out var k) ? k : default;
        if (karar.Gonderilmez)
            return new MappingResult.Skipped("Hizmet Eşleştirme ekranında \"gönderilmez\" işaretli");

        var kod = (karar.Kod ?? islem.IcbariKodu ?? "").Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(kod))
            return new MappingResult.Skipped(
                "Bakanlık hizmet kodu yok -- Hizmet Eşleştirme ekranından kod girilmeli ya da \"gönderilmez\" işaretlenmeli");

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
                    // display bakanligin KENDI listesinden okunuyor (AzProcedureCodes); listede
                    // yoksa Pusula'daki ada dusuluyor. ConditionMapper'daki ICD-10 ile ayni ilke.
                    new JsonObject
                    {
                        ["system"] = ProcedureCodeSystem,
                        ["code"] = kod,
                        ["display"] = AzProcedureCodes.Display(kod) ?? islem.IcbariAdi ?? islem.HizmetAdi ?? kod,
                    },
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
    private static string ToAzInstant(DateTime dt) => AzTime.ToAzInstant(dt);
}
