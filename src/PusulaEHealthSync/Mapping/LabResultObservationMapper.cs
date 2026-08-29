using System.Globalization;
using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId (LabResultRecord) -> AZ Observation FHIR
// resource (profile: az-lab-result-observation). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-lab-result-observation.json differential'i okunarak kesinlestirildi
// (2026-08-29).
//
// Zorunlu alanlar: status=final (sadece Status=6/onayli sonuclar cagirir, bkz. GetLabResultsByProtokolIdAsync),
// category=laboratory, code (1..1, Azerbaijan Laboratory Test Codes VS -- LOINC ile eslesiyor,
// LoincKodu bos ise gonderilemez), subject.reference (1..1), effective[x] (1..1),
// local-system-unique-id extension (1..1, base AZObservation profilinden -- ProcedureMapper/
// ConditionMapper ile AYNI kalip).
//
// procedure-code extension (1..1 -- CANLI $validate ile dogrulandi, 2026-08-29: sunucu
// bunsuz "Instance count for 'Observation.extension:procedure-code' is 0" diye reddetti).
// Icbari kodu artik GetLabResultsByProtokolIdAsync'teki LIS.Test koprusuyle geliyor (bkz. o
// metottaki gerekce) -- bulunamazsa (LOINC eslesmesi yok ya da o hizmet Icbari Sigorta Fiyat
// Listesi'nde degil) bu test sonucu SKIPPED olur, cunku zorunlu alan doldurulamaz.
public static class LabResultObservationMapper
{
    private const string LoincSystem = "http://loinc.org";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string InterpretationSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";
    private const string ProcedureCodeExtensionUrl = "http://fhir.az/StructureDefinition/procedure-code";
    private const string ProcedureCodeSystem = "http://fhir.az/CodeSystem/az-procedure-codes";

    public static MappingResult Map(LabResultRecord lab, string azPatientId, string? azEncounterId)
    {
        if (string.IsNullOrWhiteSpace(lab.LoincKodu))
            return new MappingResult.Skipped("LOINC kodu eksik -- Observation.code için zorunlu, bu test sonucu gönderilemiyor");

        if (string.IsNullOrWhiteSpace(lab.IcbariKodu))
            return new MappingResult.Skipped("İcbari Sigorta Fiyat Listesi eşleşmesi bulunamadı -- Observation.extension:procedure-code zorunlu alanı doldurulamıyor, bu test sonucu gönderilemiyor");

        var icbariKodu = lab.IcbariKodu.TrimEnd('.');

        var observation = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = $"observation-{lab.LabaratuarSonucId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-lab-result-observation" } },
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
                    new JsonObject { ["system"] = LoincSystem, ["code"] = lab.LoincKodu, ["display"] = lab.TetkikAdi ?? lab.LoincKodu },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["effectiveDateTime"] = ToAzInstant(lab.TetkikSonucOnayTarihi ?? lab.TetkikSonucTarihi ?? DateTime.Now),
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = lab.LabaratuarSonucId.ToString(),
                },
                new JsonObject
                {
                    ["url"] = ProcedureCodeExtensionUrl,
                    ["valueCodeableConcept"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            new JsonObject { ["system"] = ProcedureCodeSystem, ["code"] = icbariKodu, ["display"] = lab.IcbariAdi ?? icbariKodu },
                        },
                    },
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(azEncounterId))
            observation["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        ApplyValue(observation, lab);

        if (!string.IsNullOrWhiteSpace(lab.TetkikSonucuReferansDegeri))
        {
            observation["referenceRange"] = new JsonArray
            {
                new JsonObject { ["text"] = lab.TetkikSonucuReferansDegeri },
            };
        }

        if (lab.DisindaMi)
        {
            observation["interpretation"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = InterpretationSystem, ["code"] = "A", ["display"] = "Abnormal" },
                    },
                },
            };
        }

        return new MappingResult.Success(observation);
    }

    // Pusula sonuc alanini (TetkikSonucu) sayisal deger olarak yakalayabilirsek valueQuantity
    // (birim bilgisiyle) gonderiyoruz -- daha kullanisli/yapisal. Sayi degilse (orn. "Pozitif",
    // "Negatif", serbest metin) valueString'e duser. Turkce/Azerice veri virgulu ondalik
    // ayraci olarak kullanabiliyor -- once nokta ile normalize ediliyor.
    private static void ApplyValue(JsonObject observation, LabResultRecord lab)
    {
        if (string.IsNullOrWhiteSpace(lab.TetkikSonucu))
            return;

        var normalized = lab.TetkikSonucu.Trim().Replace(',', '.');
        if (decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            var quantity = new JsonObject { ["value"] = numeric };
            if (!string.IsNullOrWhiteSpace(lab.TetkikSonucuBirimi))
            {
                quantity["unit"] = lab.TetkikSonucuBirimi;
                quantity["system"] = "http://unitsofmeasure.org";
                quantity["code"] = lab.TetkikSonucuBirimi;
            }
            observation["valueQuantity"] = quantity;
        }
        else
        {
            observation["valueString"] = lab.TetkikSonucu;
        }
    }

    // EncounterMapper/ConditionMapper/ProcedureMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";
}
