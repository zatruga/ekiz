using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Pusula hasta.hasta -> AZ Patient FHIR resource.
// Kaynak: docs/patient-mapping.md -- kurallar orada gerekcesiyle birlikte aciklanmis,
// burada sadece uygulaniyor. Herhangi bir kural degisirse once dokumani guncelle.
public static class PatientMapper
{
    // http://fhir.az/CodeSystem/identity-document-type kod "15" -- terminology API'den
    // cekildi (2026-08-21): "Vətəndaşın fərdi identifikasiya nömrəsi" (FIN). PractitionerMapper
    // ayni kodu ayni anlamda kullaniyor.
    internal const string FinDocumentTypeDisplay = "Vətəndaşın fərdi identifikasiya nömrəsi";
    // KARAR/DUZELTME (2026-08-20): hasta.hasta.CinsiyetId ile Pusula.Cinsiyet lookup
    // tablosu (K=Kişi, Q=Qadın, D=Diğer) TUTARSIZ -- DB'de dogrulandi, gercek veride HIC
    // "Q" yok (K=171663, E=159720, D=665). Gercekte kayitlar Turkce harflerle giriliyor
    // (E=Erkek, K=Kadın), lookup tablosu ise AZ kodlariyla (kullanicinin ilettigi bilgi).
    // Yani: Pusula E (Erkek) -> AZ K (Kişi/erkek), Pusula K (Kadın) -> AZ Q (Qadın).
    // Eskiden CinsiyetId dogrudan (cevirisiz) gonderiliyordu -- bu, TUM erkek hastalari
    // (E) "desteklenmeyen kod" diye atliyor, TUM "K" (Kadın) hastalari ise yanlislikla
    // erkek (AZ K) olarak gonderiyordu. Asagidaki harita ile duzeltildi.
    private static readonly Dictionary<string, string> GenderMap = new()
    {
        ["E"] = "K", // Erkek (Pusula, Turkce) -> Kişi (AZ)
        ["K"] = "Q", // Kadın (Pusula, Turkce) -> Qadın (AZ)
    };

    // Pusula.KanGrubu.Id -> AZ http://fhir.az/CodeSystem/blood-group kodu.
    // Karsiligi olmayanlar (9,10,11,16-19: ABO/Rh belirsiz, zayif D) haritada YOK --
    // bu durumda extension:blood-group hic gonderilmez (alan opsiyonel, 0..1).
    private static readonly Dictionary<int, string> BloodGroupMap = new()
    {
        [7] = "1", // 0 Rh+  -> O(I) RH+
        [8] = "2", // 0 Rh-  -> O(I) RH-
        [3] = "3", // A Rh+  -> A(II) RH+
        [4] = "4", // A Rh-  -> A(II) RH-
        [5] = "5", // B Rh+  -> B(III) RH+
        [6] = "6", // B Rh-  -> B(III) RH-
        [1] = "7", // AB Rh+ -> AB(IV) RH+
        [2] = "8", // AB Rh- -> AB(IV) RH-
    };

    // AZ kod -> resmi display metni (terminology API, docs/sql-exports/cs_blood-group.json).
    private static readonly Dictionary<string, string> BloodGroupDisplay = new()
    {
        ["1"] = "0 (I) RH+", ["2"] = "0 (I) RH–",
        ["3"] = "A (II) RH+", ["4"] = "A (II) RH–",
        ["5"] = "B (III) RH+", ["6"] = "B (III) RH–",
        ["7"] = "AB (IV) RH+", ["8"] = "AB (IV) RH–",
    };

    // Pusula.MedeniHali.Id -> AZ http://fhir.az/CodeSystem/marital-status kodu.
    // Karsiligi olmayan (5: Belirtilmemis) haritada YOK -- maritalStatus gonderilmez.
    private static readonly Dictionary<int, string> MaritalStatusMap = new()
    {
        [1] = "1", // Evli      -> Evli
        [2] = "2", // Bekar     -> Subay
        [4] = "3", // Bosanmis  -> Bosanmis
        [3] = "4", // Dul       -> Dul
    };

    // AZ kod -> resmi display metni (terminology API, docs/sql-exports/cs_marital-status.json).
    private static readonly Dictionary<string, string> MaritalStatusDisplay = new()
    {
        ["1"] = "Evli", ["2"] = "Subay", ["3"] = "Boşanmış", ["4"] = "Dul",
    };

    // KULLANICI ISTEGI (2026-08-25): "pusulada hasta.hasta tablosunda yenidoğan tiki var ve
    // birde anne tc giriliyor" -- IsBizdeDogan=1 olan kayitlarin ~yarisinda kendi FIN'i
    // (TCKimlikNo) henuz atanmamis (dogumda normal), bu yuzden eskiden BU KAYITLAR HIC
    // GONDERILMIYORDU ("TCKimlikNo bos" diye Skipped). Resmi IG'de tam bu senaryo icin
    // az-newborn-patient profili var (bkz. StructureDefinition-az-newborn-patient.json
    // differential, 2026-08-25'te indirildi) -- kendi kimlik belgesi yoksa identifier
    // olarak SADECE anne FIN'i (system=http://fhir.az/sid/mother-fin, type/coding YOK,
    // az-patient'taki FIN slice'indan farkli) kullanilmasina izin veriyor.
    public static MappingResult Map(HastaRecord h)
    {
        if (string.IsNullOrWhiteSpace(h.Soyadi) || string.IsNullOrWhiteSpace(h.Adi))
            return new MappingResult.Skipped("Ad/soyad eksik");

        if (h.DogumTarihi is null)
            return new MappingResult.Skipped("Dogum tarihi eksik");

        if (string.IsNullOrWhiteSpace(h.CinsiyetId))
            return new MappingResult.Skipped("Cinsiyet bilgisi eksik");

        // Sandbox'ta canli dogrulandi: AZ gender-vs value set'i sadece K/Q kabul ediyor, D
        // (Diger) reddediliyor -- GenderMap'te D icin karsilik olmadigi icin zaten dusuyor.
        if (!GenderMap.TryGetValue(h.CinsiyetId, out var azGenderCode))
            return new MappingResult.Skipped($"Desteklenmeyen/eslesmeyen cinsiyet kodu: {h.CinsiyetId}");

        var hasOwnFin = !string.IsNullOrWhiteSpace(h.TCKimlikNo);
        var isNewborn = h.IsBizdeDogan && !hasOwnFin;

        if (!hasOwnFin && !isNewborn)
            return new MappingResult.Skipped("TCKimlikNo (FIN icin kullanilacak alan) bos");

        if (isNewborn && string.IsNullOrWhiteSpace(h.AnneTCKimlikNo))
            return new MappingResult.Skipped("Yenidoğan (IsBizdeDogan) ama kendi FIN'i de anne FIN'i (AnneTCKimlikNo) de bos -- az-newborn-patient icin identifier yok");

        // az-newborn-patient'ta extension:fathersName tanimli degil (yerine opsiyonel,
        // FIN-bazli extension:father-fin var -- Pusula'da baba TC'si tutulmuyor, bu yuzden
        // hic gonderilmiyor). Bu nedenle baba adi sadece normal (az-patient) yolda zorunlu.
        if (!isNewborn && string.IsNullOrWhiteSpace(h.BabaAdi))
            return new MappingResult.Skipped("Baba adi eksik (extension:fathersName zorunlu)");

        var given = new JsonArray { h.Adi };
        // az-newborn-patient: Patient.name.given max=1 -- ikinci ad (Adi2) eklenmez.
        if (!isNewborn && !string.IsNullOrWhiteSpace(h.Adi2))
            given.Add(h.Adi2);

        var extensions = new JsonArray
        {
            new JsonObject
            {
                ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                ["valueString"] = h.Id.ToString(),
            },
            new JsonObject
            {
                ["url"] = "http://fhir.az/StructureDefinition/sex",
                ["valueCode"] = azGenderCode,
            },
        };
        if (!isNewborn)
        {
            extensions.Add(new JsonObject
            {
                ["url"] = "http://fhir.az/StructureDefinition/fathersName",
                ["valueString"] = h.BabaAdi,
            });
        }

        // az-newborn-patient: Patient.identifier min=1 max=1 -- TEK identifier. Kendi
        // kimlik belgesi yoksa mother-fin slice'i kullanilir; bu slice'ta (fin/myi/dyi/
        // passport'un aksine) identifier.type hic istenmiyor, sadece system+value yeterli.
        var identifier = isNewborn
            ? new JsonObject
            {
                ["system"] = "http://fhir.az/sid/mother-fin",
                ["value"] = h.AnneTCKimlikNo,
            }
            : new JsonObject
            {
                ["type"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "http://fhir.az/CodeSystem/identity-document-type",
                            ["code"] = "15", // FIN -- karar geregi TCKimlikNo hep FIN olarak gonderiliyor
                            ["display"] = FinDocumentTypeDisplay,
                        },
                    },
                },
                ["system"] = "http://fhir.az/sid/fin",
                ["value"] = h.TCKimlikNo,
            };

        var patient = new JsonObject
        {
            ["resourceType"] = "Patient",
            ["id"] = $"patient-{h.Id}",
            ["meta"] = new JsonObject
            {
                ["profile"] = new JsonArray
                {
                    isNewborn
                        ? "http://fhir.az/StructureDefinition/az-newborn-patient"
                        : "http://fhir.az/StructureDefinition/az-patient",
                },
            },
            ["identifier"] = new JsonArray { identifier },
            ["name"] = new JsonArray
            {
                new JsonObject
                {
                    ["family"] = h.Soyadi,
                    ["given"] = given,
                },
            },
            ["birthDate"] = h.DogumTarihi.Value.ToString("yyyy-MM-dd"),
            ["extension"] = extensions,
        };

        if (h.AktifHastaId is not null)
            patient["active"] = h.AktifHastaId.Value;

        var telecom = new JsonArray();
        if (!string.IsNullOrWhiteSpace(h.GSM))
            telecom.Add(new JsonObject { ["system"] = "phone", ["use"] = "mobile", ["value"] = h.GSM });
        if (!string.IsNullOrWhiteSpace(h.SabitTel))
            telecom.Add(new JsonObject { ["system"] = "phone", ["use"] = "home", ["value"] = h.SabitTel });
        if (!string.IsNullOrWhiteSpace(h.Email))
            telecom.Add(new JsonObject { ["system"] = "email", ["value"] = h.Email });
        if (telecom.Count > 0)
            patient["telecom"] = telecom;

        if (h.KanGrubuId is not null && BloodGroupMap.TryGetValue(h.KanGrubuId.Value, out var bgCode))
        {
            patient["extension"]!.AsArray().Add(new JsonObject
            {
                ["url"] = "http://fhir.az/StructureDefinition/blood-group",
                ["valueCodeableConcept"] = new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "http://fhir.az/CodeSystem/blood-group",
                            ["code"] = bgCode,
                            ["display"] = BloodGroupDisplay[bgCode],
                        },
                    },
                },
            });
        }

        if (h.MedeniHaliId is not null && MaritalStatusMap.TryGetValue(h.MedeniHaliId.Value, out var msCode))
        {
            patient["maritalStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["system"] = "http://fhir.az/CodeSystem/marital-status",
                        ["code"] = msCode,
                        ["display"] = MaritalStatusDisplay[msCode],
                    },
                },
            };
        }

        return new MappingResult.Success(patient);
    }
}
