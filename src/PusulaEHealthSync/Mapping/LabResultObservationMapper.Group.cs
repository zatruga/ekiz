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
    private const string OtherLabCodeSystem = "http://fhir.az/CodeSystem/az-other-lab-test-codes";

    // az-other-lab-test-codes CodeSystem'de TEK BIR kod var: "other"
    // (content: complete, indirildi 2026-09-23). Yerel test kodumuzu o sisteme
    // yazmak GECERSIZ -- Observation.code ve component.code ikisi de
    // az-lab-test-codes-vs'e REQUIRED bagli, yani ICD-10'daki D38.1 reddinin
    // aynisi olurdu.
    private const string OtherLabCode = "other";
    private const string OtherLabDisplay = "Digər laboratoriya testi";

    // Yerel (LOINC olmayan) test kodlarini tasiyan sistem. Bakanliktan resmi bir
    // URI istenmeli; o gelene kadar kaynak sistemi acikca isaretleyen bu URI
    // kullaniliyor. REQUIRED baglama bundan ETKILENMIYOR: CodeableConcept icinde
    // ValueSet'te bulunan EN AZ BIR coding olmasi yeterli, onu "other" sagliyor.
    private const string YerelLabCodeSystem = "http://pusula.local/CodeSystem/lab-test";

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

    // BAKANLIK ISTEGI (2026-09-16, madde 7): display alanina "PDW" gibi lokal kisaltma
    // degil kodun STANDART aciklamasi yazilmali; lokal ad code.text'e gitmeli.
    //
    // ARTIK YAPILIYOR. Standart aciklamalar Loinc sinifindan geliyor (tx.fhir.org'dan
    // cekilip gomuldu, LOINC 2.82) -- neden IG'den degil de oradan alindigi Loinc.cs'te.
    //
    // Kod cozumlemesi de bicim yerine KONTROL BASAMAGINA dayaniyor (Loinc.Coz):
    //   - gecerli LOINC            -> loinc.org + kodun kendisi
    //   - gecerli LOINC + yerel ek -> loinc.org + TABAN kod (1533-9-A -> 1533-9)
    //   - digerleri                -> az-other-lab-test-codes "other" + yerel kod
    // Olculdu (son 365 gun, 1.555.441 lab istemi): %49,1 dogrudan gecerli LOINC,
    // %7,3 taban koda inerek kurtariliyor, kalani "other" ile gidiyor.
    private static JsonObject Kodlama(string kod, string lokalAd)
    {
        var ham = kod.Trim();
        var loinc = Loinc.Coz(ham);

        if (loinc is not null)
        {
            return new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = LoincSystem,
                        ["code"] = loinc,
                        // Tabloda yoksa lokal ada dusuluyor -- display profilde 1..1.
                        ["display"] = Loinc.Display(loinc) ?? lokalAd,
                    },
                },
                ["text"] = lokalAd,
            };
        }

        // LOINC degil: baglamayi "other" sagliyor, yerel kod IKINCI coding olarak
        // korunuyor -- bilgi kaybi olmadan gecerli kaynak.
        return new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject
                {
                    ["system"] = OtherLabCodeSystem,
                    ["code"] = OtherLabCode,
                    ["display"] = OtherLabDisplay,
                },
                new JsonObject
                {
                    ["system"] = YerelLabCodeSystem,
                    ["code"] = ham,
                    ["display"] = lokalAd,
                },
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
