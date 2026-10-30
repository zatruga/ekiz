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
        IReadOnlyDictionary<int, (string? Kod, bool Gonderilmez)>? eslestirme = null,
        string? azPractitionerId = null)
    {
        // HIZMET ESLESTIRME EKRANININ KARARI (HizmetMappingStore, 2026-09-24).
        // Iki ayri sey soyleyebiliyor:
        //   Gonderilmez -> bu kalem bakanliga hic gitmez (yatak ucreti, recete islemi,
        //                  CD ucreti gibi TIBBI ISLEM OLMAYAN kalemler)
        //   Kod         -> elle girilen bakanlik kodu; yoksa Pusula'nin CANLI Icbari kodu
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

        // ===== performer -- ISLEMI YAPAN SAGLIK CALISANI (2026-10-13) =====
        //
        // BAKANLIK: "Bu kayit 'İxtisaslı həkim müayinəsi' hizmetini iceriyor. Ancak
        // performer alaninda muayeneyi gerceklestiren hekime ait referans bulunmuyor."
        // (ornek: Procedure/01a0fc8f-96a8-7197-931c-089e78adfd33)
        //
        // Kaynak Hasta.ProtokolIslem.UygulayanPersonelId (kullanici onayi 2026-10-13).
        //
        // 0..* OLDUGU ICIN BOS BIRAKILABILIR ve bu KASITLI bir yol: cozulemeyen ya da
        // gonderilemeyen bir personel icin UYDURMA referans yazmaktansa alan hic
        // yazilmiyor. Cagiran taraf (ProcedureSyncService) Practitioner'i once gondermeye
        // calisir; basarisizsa buraya null gelir ve islem yine de gider -- performer
        // ugruna butun Procedure'u kaybetmek dogru olmazdi.
        //
        // function (islemdeki rol) EKLENMIYOR: Pusula'da rolu soyleyen bir alan yok,
        // personel TIPI ise rol degil unvan. Uydurma rol kodu gondermektense alan bos
        // (0..1).
        if (!string.IsNullOrWhiteSpace(azPractitionerId))
        {
            procedure["performer"] = new JsonArray
            {
                new JsonObject
                {
                    ["actor"] = new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" },
                },
            };
        }

        return new MappingResult.Success(procedure);
    }

    // EncounterMapper/ConditionMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => AzTime.ToAzInstant(dt);
}
