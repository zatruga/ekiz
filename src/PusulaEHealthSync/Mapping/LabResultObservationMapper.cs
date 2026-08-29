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
// KASITLI EKSIK: extension:procedure-code (profilde 1..1 zorunlu gorunuyor, bkz.
// docs/bakanlik-sorulari.md soru #2) BURAYA EKLENMEDI -- LIS view'i (ayri bir bagli sunucudaki
// COMED LIS sisteminden geliyor, bkz. LabResultRecord ustundeki not) Hasta.ProtokolIslem/Ortak.
// Hizmet'e baglanan bir kolon DONDURMUYOR, yani Icbari/prosedur koduna ulasacak bir JOIN yok.
// KULLANICI KARARI (2026-08-29): "once canli $validate ile test et" -- sunucunun bu alani
// GERCEKTEN reddedip reddetmedigini once gorelim, teoriye gore degil.
public static class LabResultObservationMapper
{
    private const string LoincSystem = "http://loinc.org";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string InterpretationSystem = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation";

    public static MappingResult Map(LabResultRecord lab, string azPatientId, string? azEncounterId)
    {
        if (string.IsNullOrWhiteSpace(lab.LoincKodu))
            return new MappingResult.Skipped("LOINC kodu eksik -- Observation.code için zorunlu, bu test sonucu gönderilemiyor");

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
