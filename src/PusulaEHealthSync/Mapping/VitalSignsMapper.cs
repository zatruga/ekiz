using System.Globalization;
using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Vital bulgular -> AZ Observation (profile: az-observation).
// Kaynak: https://fhir.e-health.gov.az/StructureDefinition-az-observation.json
//
// BAKANLIK ISTEGI (2026-09-16, madde 4): "Laboratuvar sonuclari icin
// az-lab-result-observation, ATES GIBI KLINIK OLCUMLER ve doktor notlari icin ise
// az-observation (General Observation) profili kullaniliyor. Inceledigim orneklerde
// az-observation profiliyle yapilmis bir gonderime rastlamadim."
//
// Zorunlu alanlar (StructureDefinition'dan): extension:local-system-unique-id (1..1),
// status (1..1), category (1..1, required binding observation-category), code (1..1),
// subject.reference (1..1), encounter.reference (1..1 -- encounter verilirse),
// effective[x] (1..1).
//
// KAN BASINCI TEK KAYNAK: FHIR'in yerlesik kalibi sistolik+diastolik'i AYRI iki
// Observation olarak degil, 85354-9 panel kodu altinda IKI COMPONENT olarak gonderir.
// Bakanligin laboratuvar icin istedigi yapinin (madde 5) aynisi. Yalnizca biri
// olculmusse panel kurulmaz, o olcum kendi koduyla tek basina gider.
//
// LOINC display'leri BURADA STANDART: bakanlik madde 7'de "display alanina yalnizca
// lokal kisa isimler yazilmamali, kodun standart aciklamasi kullanilmali" dedi.
// Bu dokuz kod LOINC'un vital-signs cekirdegi, standart adlari bilindigi icin
// dogrudan yazilabiliyor (laboratuvardaki gibi bir kaynak sorunu yok).
public static class VitalSignsMapper
{
    private const string LoincSystem = "http://loinc.org";
    private const string UcumSystem = "http://unitsofmeasure.org";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string LocalIdExtensionUrl = "http://fhir.az/StructureDefinition/local-system-unique-id";

    private const string BpPanelKodu = "85354-9";
    private const string BpPanelAdi = "Blood pressure panel with all children optional";

    private record Tanim(string Loinc, string Display, string Birim, string BirimKodu);

    private static readonly Dictionary<VitalTur, Tanim> Tanimlar = new()
    {
        [VitalTur.Ates] = new("8310-5", "Body temperature", "°C", "Cel"),
        [VitalTur.Nabiz] = new("8867-4", "Heart rate", "/min", "/min"),
        [VitalTur.Sistolik] = new("8480-6", "Systolic blood pressure", "mmHg", "mm[Hg]"),
        [VitalTur.Diastolik] = new("8462-4", "Diastolic blood pressure", "mmHg", "mm[Hg]"),
        [VitalTur.Spo2] = new("2708-6", "Oxygen saturation in Arterial blood", "%", "%"),
        [VitalTur.Boy] = new("8302-2", "Body height", "cm", "cm"),
        [VitalTur.Kilo] = new("29463-7", "Body weight", "kg", "kg"),
        [VitalTur.Vki] = new("39156-5", "Body mass index (BMI) [Ratio]", "kg/m2", "kg/m2"),
        [VitalTur.Vya] = new("8277-6", "Body surface area", "m2", "m2"),
    };

    // Ekranda ve gunlukte gorunen ad -- kullanici hangi olcumun gittigini tanisin diye.
    public static string TurAdi(VitalTur tur) => tur switch
    {
        VitalTur.Ates => "Ateş",
        VitalTur.Nabiz => "Nabız",
        VitalTur.Sistolik => "Kan basıncı (sistolik)",
        VitalTur.Diastolik => "Kan basıncı (diastolik)",
        VitalTur.Spo2 => "SpO2",
        VitalTur.Boy => "Boy",
        VitalTur.Kilo => "Kilo",
        VitalTur.Vki => "Vücut Kitle İndeksi",
        VitalTur.Vya => "Vücut Yüzey Alanı",
        _ => tur.ToString(),
    };

    // Gonderilecek tek bir vital kaynagi: yerel kimlik + ekrandaki ad + FHIR govdesi.
    public record VitalKaynak(string LocalId, string Ad, JsonObject Resource);

    public static List<VitalKaynak> Map(
        GenelMuayeneRecord muayene, ProtokolListItem p, string azPatientId, string? azEncounterId)
    {
        var olcumler = VitalBulguParser.Ayristir(muayene.Bulgulari);
        if (olcumler.Count == 0) return [];

        var deger = olcumler.ToDictionary(o => o.Tur, o => o.Deger);
        var zaman = AzTime.ToAzInstant(
            muayene.MuayeneBaslangicTarihi ?? muayene.ModifiedDate ?? muayene.CreatedDate);

        var sonuc = new List<VitalKaynak>();

        // 1) Kan basinci -- ikisi birden varsa TEK panel kaynagi.
        var sistolikVar = deger.TryGetValue(VitalTur.Sistolik, out var sistolik);
        var diastolikVar = deger.TryGetValue(VitalTur.Diastolik, out var diastolik);
        if (sistolikVar && diastolikVar)
        {
            var panel = Iskelet(p, azPatientId, azEncounterId, zaman, BpPanelKodu, BpPanelAdi, "Kan basıncı");
            panel["component"] = new JsonArray
            {
                Bilesen(VitalTur.Sistolik, sistolik),
                Bilesen(VitalTur.Diastolik, diastolik),
            };
            sonuc.Add(new VitalKaynak(LocalId(p, BpPanelKodu), "Kan basıncı", panel));
        }

        // 2) Geri kalan her olcum kendi kaynagi. Panel kuruldu ise sistolik/diastolik
        //    ayrica gonderilmez -- mukerrer olurdu.
        foreach (var (tur, d) in deger)
        {
            if (sistolikVar && diastolikVar && tur is VitalTur.Sistolik or VitalTur.Diastolik) continue;

            var t = Tanimlar[tur];
            var ad = TurAdi(tur);
            var obs = Iskelet(p, azPatientId, azEncounterId, zaman, t.Loinc, t.Display, ad);
            obs["valueQuantity"] = Miktar(t, d);
            sonuc.Add(new VitalKaynak(LocalId(p, t.Loinc), ad, obs));
        }

        return sonuc;
    }

    // Yerel kimlik: protokol + LOINC. FindExistingIdAsync bu deger uzerinden arama
    // yaptigi icin AYNI protokolun ayni olcumu tekrar gonderildiginde Create degil
    // Update olur. Bir protokolde birden fazla GenelMuayene kaydi olabilir ama
    // gonderilen HER ZAMAN epikrizi tasiyan tek kayit (bkz. repository'deki not),
    // o yuzden protokol bazli kimlik kararli.
    private static string LocalId(ProtokolListItem p, string loinc) => $"{p.ProtokolId}-vital-{loinc}";

    private static JsonObject Iskelet(
        ProtokolListItem p, string azPatientId, string? azEncounterId, string zaman,
        string loinc, string display, string yerelAd)
    {
        var obs = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = $"vital-{p.ProtokolId}-{loinc}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-observation" } },
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = LocalIdExtensionUrl,
                    ["valueString"] = LocalId(p, loinc),
                },
            },
            ["status"] = "final",
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = CategorySystem, ["code"] = "vital-signs", ["display"] = "Vital Signs" },
                    },
                },
            },
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = loinc, ["display"] = display },
                },
                // BAKANLIK ISTEGI (madde 7): lokal ad code.text'te, standart aciklama display'de.
                ["text"] = yerelAd,
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["effectiveDateTime"] = zaman,
        };

        if (azEncounterId is not null)
            obs["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        return obs;
    }

    private static JsonObject Bilesen(VitalTur tur, decimal deger)
    {
        var t = Tanimlar[tur];
        return new JsonObject
        {
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = t.Loinc, ["display"] = t.Display },
                },
                ["text"] = TurAdi(tur),
            },
            ["valueQuantity"] = Miktar(t, deger),
        };
    }

    private static JsonObject Miktar(Tanim t, decimal deger) => new()
    {
        ["value"] = JsonValue.Create(deger),
        ["unit"] = t.Birim,
        ["system"] = UcumSystem,
        ["code"] = t.BirimKodu,
    };

    // Ekranda ozet gostermek icin -- "Ateş 36,6 °C · Nabız 77 /min" gibi.
    public static string Ozet(VitalTur tur, decimal deger)
    {
        var t = Tanimlar[tur];
        return $"{TurAdi(tur)} {deger.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR"))} {t.Birim}";
    }
}
