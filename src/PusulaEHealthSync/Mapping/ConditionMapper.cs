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
    private const string DiagnosisTypeExtensionUrl = "http://fhir.az/StructureDefinition/diagnosis-type";
    private const string DiagnosisTypeSystem = "http://fhir.az/CodeSystem/diagnosis-type";

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
            // BAKANLIK ISTEGI (2026-09-16): aciklama, yerel terminoloji servisindeki
            // STANDART Azerbaycanca metin olmali. Pusula'nin kendi ICD tablosu karisik
            // (bir kismi Turkce), o yuzden once AZ CodeSystem'inden okunuyor; kod AZ
            // listesinde yoksa Pusula metnine dusuluyor -- ama o kod zaten sunucu
            // tarafindan reddedilir (bkz. ICD-10 ValueSet boslugu maddesi).
            //
            // text: Pusula'da gorunen ad. Kullanici hangi kaydin gonderildigini
            // eslestirebilsin diye korunuyor; display ile farkliysa bilgi kaybi olmaz.
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = IcdSystem,
                        ["code"] = tani.Kodu,
                        ["display"] = AzIcd10.Display(tani.Kodu) ?? tani.Adi ?? tani.Kodu,
                    },
                },
                ["text"] = tani.Adi ?? tani.Kodu,
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
                // BAKANLIK ISTEGI (2026-09-16): "Tanı türü önemli bir bilgi; kaynak
                // sistemde tutuluyorsa gönderilmesini rica ederim."
                //
                // Pusula'da karsiligi Tedavi.ProtokolICD.IsBirincilTani (611.518 taninin
                // 526.578'inde isaretli, olculdu). Kod listesi (az-icd CodeSystem
                // diagnosis-type): 1 = Əsas diaqnoz, 2 = əlavə diaqnoz,
                // 3 = Yanaşı xəstəliklər.
                //
                // KOD 3 BILEREK GONDERILMIYOR: Pusula'da komorbiditeyi isaretleyen ayri
                // bir alan yok. IsAnaTani var ama anlami dogrulanmadi (100.168 evet,
                // 84.168 NULL) -- dogrulamadan "yanasi xestelik" demek taniyi YANLIS
                // etiketlemek olurdu.
                new JsonObject
                {
                    ["url"] = DiagnosisTypeExtensionUrl,
                    ["valueCodeableConcept"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            tani.IsBirincilTani
                                ? new JsonObject { ["system"] = DiagnosisTypeSystem, ["code"] = "1", ["display"] = "Əsas diaqnoz" }
                                : new JsonObject { ["system"] = DiagnosisTypeSystem, ["code"] = "2", ["display"] = "əlavə diaqnoz" },
                        },
                    },
                },
            },
        };

        return new MappingResult.Success(condition);
    }

    // EncounterMapper/CompositionMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => AzTime.ToAzInstant(dt);
}
