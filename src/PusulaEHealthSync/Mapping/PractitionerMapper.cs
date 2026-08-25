using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Pusula IK.Personel -> AZ Practitioner FHIR resource.
// Kaynak: https://fhir.e-health.gov.az/StructureDefinition-az-practitioner.json (resmi IG,
// 2026-08-24'te dogrulandi -- bkz. konusma). Profil kucuk: identifier (fin/myi/dyi
// slice'larindan biri, biz sadece fin gonderiyoruz -- toplam [1..1]), active (patternBoolean=
// true, SABIT), name (1..1). qualification profilde HIC gecmiyor, gonderilmiyor.
public static class PractitionerMapper
{
    public static MappingResult Map(PersonelRecord d)
    {
        if (d.PersonelTipiId != PersonelRecord.DoktorTipiId)
            return new MappingResult.Skipped($"PersonelTipiId={d.PersonelTipiId} -- sadece Doktor (1) Practitioner olarak gonderiliyor");

        if (string.IsNullOrWhiteSpace(d.Adi) || string.IsNullOrWhiteSpace(d.Soyadi))
            return new MappingResult.Skipped("Ad/soyad eksik");

        if (string.IsNullOrWhiteSpace(d.TCKimlikNo))
            return new MappingResult.Skipped("TCKimlikNo (FIN icin kullanilacak alan) bos");

        var practitioner = new JsonObject
        {
            ["resourceType"] = "Practitioner",
            ["id"] = $"practitioner-{d.Id}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-practitioner" } },
            ["identifier"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["system"] = "http://fhir.az/CodeSystem/identity-document-type",
                                ["code"] = "15", // FIN -- Patient ile ayni karar (bkz. patient-mapping.md)
                                ["display"] = PatientMapper.FinDocumentTypeDisplay,
                            },
                        },
                    },
                    ["system"] = "http://fhir.az/sid/fin",
                    ["value"] = d.TCKimlikNo,
                },
            },
            // local-system-unique-id bir IDENTIFIER degil, extension -- Patient/Encounter
            // ile ayni desen (bkz. PatientMapper/EncounterMapper). EHealthClient.FindExistingIdAsync
            // bu extension'i arayan bir arama parametresi kullaniyor.
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = d.Id.ToString(),
                },
            },
            // DUZELTME (2026-08-24, canli hatada bulundu -- DoktorId=2740 icin 7 kez ust
            // uste basarisiz oldu): Practitioner.active, az-practitioner IG'sinde SABIT
            // pattern=true (bkz. StructureDefinition-az-practitioner.json differential) --
            // CikisTarihi'ne (Pusula'da ayrilmis doktor) gore false gonderilince sunucu
            // HER SEFERINDE "Value does not match pattern 'true'" ile reddediyordu. Bu alan
            // bu API uzerinden aktif/pasif ayrimi TASIMIYOR (bakanlik bunu baska bir yerden
            // yonetiyor olmali) -- her zaman true.
            ["active"] = true,
            ["name"] = new JsonArray
            {
                new JsonObject
                {
                    ["family"] = d.Soyadi,
                    ["given"] = new JsonArray { d.Adi },
                },
            },
        };

        return new MappingResult.Success(practitioner);
    }
}
