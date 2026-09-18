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

    public static MappingResult Map(
        IcdTaniRecord tani, ProtokolListItem p, string azPatientId, string azEncounterId, int protokoldekiTaniSayisi)
    {
        // KULLANICI DUZELTMESI (2026-09-18): "pusulada tanilar birinci ikinci degilde
        // on tani kesin tani seklindedir". Dogrulandi -- kaynak Pusula'nin KENDI
        // stored procedure'u Tedavi.usp_GetEpikrizTani:
        //     CASE MedulaTaniTipiId WHEN 1 THEN 'On Tani' WHEN 2 THEN 'Kesin Tani'
        //                           WHEN 3 THEN 'Ayirici Tani' END
        // Bu bir KESINLIK ekseni; HL7'nin condition-ver-status ValueSet'i ile birebir
        // ortusuyor (az-condition'da verificationStatus 0..1, required binding).
        //
        // ONCEDEN HER TANIDA SABIT "confirmed" GIDIYORDU -- son 365 gunde 137.998
        // protokolun 98.188'inde (%71) en az bir ON TANI vardi; hepsi bakanliga
        // "kesinlesmis" olarak bildirilmisti. Bu duzeltmenin asil sebebi bu.
        var dogrulamaDurumu = VerificationStatus(tani.TaniTipiKodu);

        var condition = new JsonObject
        {
            ["resourceType"] = "Condition",
            ["id"] = $"condition-{p.ProtokolId}-{tani.ICDId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-condition" } },
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
            },
        };

        // BAKANLIK ISTEGI (2026-09-16): "Tanı türü önemli bir bilgi; kaynak sistemde
        // tutuluyorsa gönderilmesini rica ederim." Kod listesi (diagnosis-type):
        // 1 = Əsas diaqnoz, 2 = əlavə diaqnoz, 3 = Yanaşı xəstəliklər -- bu bir SIRA
        // eksenidir (hangisi ana tani), kesinlik ekseni DEGIL (o verificationStatus).
        //
        // PUSULA'DA BU EKSENIN KAYNAGI YOK. Olculdu (son 365 gun, 186.038 kayit):
        //   IsBirincilTani : protokol basina TEK DEGIL -- 2 tanili 17.351 protokolde
        //                    IKISINDE de 1, 4 tanili 2.367 protokolde DORDUNDE de 1.
        //                    Bununla "esas tani" demek 4 tane esas tani gondermek olurdu.
        //   IsEkTani       : %100 NULL
        //   SiraNo         : %100 NULL
        //   IsAnaTani      : MedulaTaniTipiId=2 ile birebir ortusuyor -- bagimsiz bilgi
        //                    degil, o da kesinlik ekseni.
        //
        // KULLANICI KARARI (2026-09-18): protokolde TEK tani varsa o tani mantiksal
        // zorunlulukla esas tanidir -- orada kod 1 gonderilir. Birden fazla taninin
        // oldugu protokolde hangisinin esas oldugu bilinmiyor, alan HIC gonderilmez
        // (0..1, bos birakmak profili bozmaz). Son 365 gunde 137.998 protokolun
        // 108.494'u (%78) tek tanili -- yani talebin buyuk kismi karsilaniyor.
        //
        // NOT: sayim GONDERILEBILIR tanilari kapsar; ICD kodu bos olan satirlar
        // GetTanilarByProtokolIdAsync'te zaten eleniyor (nadir).
        if (protokoldekiTaniSayisi == 1)
        {
            condition["extension"]!.AsArray().Add(new JsonObject
            {
                ["url"] = DiagnosisTypeExtensionUrl,
                ["valueCodeableConcept"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = DiagnosisTypeSystem, ["code"] = "1", ["display"] = "Əsas diaqnoz" },
                    },
                },
            });
        }

        // Kaynakta tani tipi yoksa (son 365 gunde 1.327 kayit) alan HIC gonderilmiyor.
        // verificationStatus 0..1 oldugu icin bos birakmak profili bozmaz; uydurma bir
        // kesinlik iddia etmektense hic iddia etmemek dogru.
        if (dogrulamaDurumu is not null)
            condition["verificationStatus"] = dogrulamaDurumu;

        return new MappingResult.Success(condition);
    }

    // MedulaTaniTipiId -> HL7 condition-ver-status. Pusula'nin uc degeri de standart
    // ValueSet'te birebir karsilik buluyor, esleme zorlanmadan oturuyor.
    private static JsonObject? VerificationStatus(string? taniTipiKodu) => taniTipiKodu switch
    {
        "1" => Kodlama("provisional", "Provisional"),
        "2" => Kodlama("confirmed", "Confirmed"),
        "3" => Kodlama("differential", "Differential"),
        _ => null,
    };

    private static JsonObject Kodlama(string kod, string ad) => new()
    {
        ["coding"] = new JsonArray
        {
            new JsonObject { ["system"] = VerificationStatusSystem, ["code"] = kod, ["display"] = ad },
        },
    };

    // EncounterMapper/CompositionMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => AzTime.ToAzInstant(dt);
}
