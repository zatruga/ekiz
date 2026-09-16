using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// BAKANLIK ISTEGI (2026-09-16, "öncelikli düzeltme konusu"):
//
//   "Alt parametreli tetkikler ana tetkik altında gruplanmalı; ana tetkik Observation,
//    alt parametreler ise aynı kaynağın component dizisi içerisinde gönderilmelidir.
//    Ana tetkikin kodu, hizmet kodu ve yerel kayıt ID'si Observation seviyesinde; her
//    alt parametrenin kodu, sonuç değeri, birimi ve varsa referans aralığı ilgili
//    component içerisinde bulunmalıdır. Alt parametresi olmayan, tek sonuçlu bağımsız
//    tetkiklerde sonuç doğrudan Observation.value[x] alanında gönderilebilir."
//
// Profil bunu zaten destekliyor: az-lab-result-observation'da Observation.component
// ust seviyeyle AYNI yapida tanimli (component.code.coding 1..*, component.value[x] 1..1).
public static partial class LabResultObservationMapper
{
    // LOINC kod bicimi: rakamlar + tire + TEK kontrol rakami (orn. 30522-7).
    // Pusula'da bu bicime uymayan kodlar da var (MP15227, EH-011, LE-001, 9397-1-B,
    // 5803-21) -- bunlar LOINC DEGIL. Hepsini loinc.org sistemiyle etiketlemek yanlisti;
    // az-lab-test-codes-vs zaten LOINC'da olmayan testler icin ikinci bir sistem
    // (az-other-lab-test-codes) kapsiyor, artik oraya yazilıyorlar.
    [GeneratedRegex(@"^\d{1,6}-\d$")]
    private static partial Regex LoincPattern { get; }

    private const string OtherLabCodeSystem = "http://fhir.az/CodeSystem/az-other-lab-test-codes";

    private static string KodSistemi(string kod) =>
        LoincPattern.IsMatch(kod.Trim()) ? LoincSystem : OtherLabCodeSystem;

    public static MappingResult MapGroup(
        LabGroupBuilder.LabGrup grup, string azPatientId, string? azEncounterId)
    {
        var anahtar = grup.AnahtarSatir;

        // Observation SEVIYESINDEKI kod: panelli grupta panelin kendi kodu, tek sonuclu
        // testte satirin kendi kodu. Panelin kendi satiri gelmese bile PanelLoincKodu
        // dolu oldugu icin kod bulunabiliyor (bkz. LabResultRecord'daki not).
        var kod = grup.Panelli
            ? (Bos(anahtar.PanelLoincKodu) ? anahtar.LoincKodu : anahtar.PanelLoincKodu)
            : anahtar.LoincKodu;
        if (Bos(kod))
            return new MappingResult.Skipped(
                "LOINC/test kodu eksik -- Observation.code için zorunlu, bu tetkik gönderilemiyor");

        // procedure-code: panelli grupta HER ZAMAN panelin Icbari kodu (alt parametrelerin
        // kendi kodu genelde yok, panel bir butun olarak faturalaniyor).
        var icbari = grup.Panelli
            ? (Bos(anahtar.PanelIcbariKodu) ? anahtar.IcbariKodu : anahtar.PanelIcbariKodu)
            : anahtar.IcbariKodu;
        if (Bos(icbari))
            return new MappingResult.Skipped(
                "İcbari Sigorta Fiyat Listesi eşleşmesi bulunamadı -- Observation.extension:procedure-code zorunlu alanı doldurulamıyor");

        var icbariAdi = (grup.Panelli && !Bos(anahtar.PanelIcbariAdi) ? anahtar.PanelIcbariAdi : anahtar.IcbariAdi)
                        ?? icbari!;
        var icbariKodu = icbari!.TrimEnd('.');
        var gosterimAdi = grup.Panelli ? grup.PanelAdi : (anahtar.TetkikAdi ?? kod!);

        var observation = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = $"observation-{grup.AnahtarId}",
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
            ["code"] = Kodlama(kod!, gosterimAdi),
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["effectiveDateTime"] = AzTime.ToAzInstant(
                anahtar.TetkikSonucOnayTarihi ?? anahtar.TetkikSonucTarihi ?? DateTime.Now),
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = grup.AnahtarId.ToString(),
                },
                new JsonObject
                {
                    ["url"] = ProcedureCodeExtensionUrl,
                    ["valueCodeableConcept"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            new JsonObject { ["system"] = ProcedureCodeSystem, ["code"] = icbariKodu, ["display"] = icbariAdi },
                        },
                    },
                },
            },
        };

        if (!Bos(azEncounterId))
            observation["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        if (grup.Panelli)
        {
            // component.value[x] profilde 1..1 ZORUNLU -- degeri olmayan alt parametre
            // gecerli bir component degildir, sessizce disarida birakilir.
            var bilesenler = new JsonArray();
            foreach (var alt in grup.Bilesenler)
            {
                if (Bos(alt.TetkikSonucu) || Bos(alt.LoincKodu)) continue;

                var c = new JsonObject { ["code"] = Kodlama(alt.LoincKodu!, alt.TetkikAdi ?? alt.LoincKodu!) };
                Deger(c, alt);
                if (!Bos(alt.TetkikSonucuReferansDegeri))
                    c["referenceRange"] = new JsonArray { new JsonObject { ["text"] = alt.TetkikSonucuReferansDegeri } };
                bilesenler.Add(c);
            }

            if (bilesenler.Count == 0)
                return new MappingResult.Skipped(
                    $"\"{grup.PanelAdi}\" panelinde sonuç değeri taşıyan alt parametre yok -- component üretilemedi");

            observation["component"] = bilesenler;

            // Panelin KENDI satirinda da bir deger varsa (nadir) Observation.value[x]'e
            // yazilir; profil value ile component'in birlikte bulunmasina izin veriyor.
            if (!Bos(anahtar.TetkikSonucu) && anahtar.TetkikAdi == grup.PanelAdi)
                Deger(observation, anahtar);
        }
        else
        {
            if (Bos(anahtar.TetkikSonucu))
                return new MappingResult.Skipped(
                    "Bu tetkikin sonuç değeri yok -- tek sonuçlu bir Observation olarak gönderilemez");

            Deger(observation, anahtar);
            if (!Bos(anahtar.TetkikSonucuReferansDegeri))
                observation["referenceRange"] = new JsonArray
                {
                    new JsonObject { ["text"] = anahtar.TetkikSonucuReferansDegeri },
                };
        }

        return new MappingResult.Success(observation);
    }

    // BAKANLIK NOTU (2026-09-16): display alanina "PDW" gibi lokal kisaltma degil, kodun
    // STANDART acıklamasi yazilmali; lokal ad code.text'e gitmeli.
    //
    // text KISMI SIMDI YAPILDI. display hala lokal ad, cunku standart LOINC acıklamalarinin
    // bir kaynagi YOK: az-lab-test-codes-vs dogrudan http://loinc.org'u kapsiyor ve IG
    // CodeSystem'i yayinlamiyor (CodeSystem-az-lab-test-codes.json HTML donuyor). display
    // profilde 1..1 zorunlu oldugu icin bos birakilamiyor. LOINC tablosu projeye eklenince
    // burasi tek satirda duzelir (bkz. docs/bakanlik-geri-bildirim-2026-09-16.md, madde 7).
    private static JsonObject Kodlama(string kod, string lokalAd)
    {
        var k = kod.Trim();
        return new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject { ["system"] = KodSistemi(k), ["code"] = k, ["display"] = lokalAd },
            },
            ["text"] = lokalAd,
        };
    }

    // Tek satirlik Map'teki ApplyValue ile AYNI kural (sayisalsa valueQuantity, birim yoksa
    // valueString) -- component icin de gecerli oldugundan ortak kullaniliyor.
    private static void Deger(JsonObject hedef, LabResultRecord lab)
    {
        if (Bos(lab.TetkikSonucu)) return;

        var normalized = lab.TetkikSonucu!.Trim().Replace(',', '.');
        if (decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var sayi)
            && !Bos(lab.TetkikSonucuBirimi))
        {
            hedef["valueQuantity"] = new JsonObject
            {
                ["value"] = sayi,
                ["unit"] = lab.TetkikSonucuBirimi,
                ["system"] = "http://unitsofmeasure.org",
                ["code"] = lab.TetkikSonucuBirimi,
            };
        }
        else
        {
            hedef["valueString"] = lab.TetkikSonucu;
        }
    }

    private static bool Bos(string? s) => string.IsNullOrWhiteSpace(s);
}
