using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Pusula hasta.protokol -> AZ Encounter FHIR resource.
// Kaynak: docs/encounter-mapping.md -- kurallar orada gerekcesiyle birlikte aciklanmis,
// burada sadece uygulaniyor. serviceProvider (Organization/5204) bakanlik cevabiyla
// (2026-08-20) kesinlesti, sabit deger olarak kullaniliyor -- tek hastane.
//
// subject (Patient) ve participant (Practitioner) referanslari bu sinifin disinda,
// EncounterSyncService tarafinda e-Health'te arama yapilarak cozulur (local-system-unique-id
// ile) -- Map buraya sadece SONUCU (AZ FHIR id) parametre olarak alir, kendisi HTTP/DB
// erisimi yapmaz (PatientMapper ile ayni saflik ilkesi).
public static class EncounterMapper
{
    // AZ CodeSystem http://fhir.az/CodeSystem/hospital-departments (52 kod: 51 isimli +
    // "999"/Digər, terminology API'den cekildi -- bkz. docs/sql-exports/cs_hospital-departments.json).
    // Sadece referans/dropdown amacli (Bolum Eslestirme sayfasi) -- Map artik bunu OTOMATIK
    // eslestirme icin kullanmiyor, bkz. asagidaki KARAR notu.
    //
    // DUZELTME (2026-08-21, canli olayda bulundu): "999" (Digər) bu listede EKSIKTI --
    // Bolum Eslestirme sayfasinda bir bolum "999" ile eslestirilince, serviceType.coding.display
    // GetValueOrDefault("999") -> null donuyordu, bu da JSON'a LITERAL "display":null olarak
    // yaziliyordu (alan hic gonderilmemis GIBI degil, GERCEKTEN null gonderiliyordu) --
    // bakanlik uygulamasinda bolum adi yine gorunmuyordu. 999 eklendi + Map() artik null
    // display'i hic eklemiyor (asagida bkz.).
    public static readonly Dictionary<string, string> HospitalDepartments = new()
    {
        ["999"] = "Digər",
        ["2"] = "Pulmonologiya",
        ["3"] = "Revmatologiya",
        ["4"] = "Kardiologiya",
        ["5"] = "Cərrahiyyə",
        ["6"] = "Qastroenterologiya",
        ["7"] = "Endokrinologiya",
        ["8"] = "Allerqologiya-İmmunologiya",
        ["9"] = "Böyüklər üçün yoluxucu xəstəliklər",
        ["10"] = "Uşaqlar üçün yoluxucu xəstəliklər",
        ["15"] = "Travmatologiya (ortopedik)",
        ["16"] = "Urologiya",
        ["17"] = "Onkologiya",
        ["18"] = "Radiologiya",
        ["19"] = "Stomatologiya",
        ["21"] = "Mama-ginekologiya",
        ["24"] = "Oftalmologiya",
        ["25"] = "Otolarinqologiya",
        ["26"] = "Surdologiya",
        ["27"] = "Vərəm",
        ["29"] = "Nevrologiya",
        ["30"] = "Ruhi xəstəliklər",
        ["31"] = "Psixoterapiya",
        ["32"] = "Psixoendokrinologiya",
        ["33"] = "Narkologiya",
        ["34"] = "Yeniyetmələr üçün narkologiya",
        ["35"] = "Anonim müalicə üçün narkologiya",
        ["36"] = "Dəri-zöhrəvi",
        ["37"] = "Bərpaedici müalicə",
        ["47"] = "Qanköçürmə",
        ["48"] = "Hemodializ",
        ["49"] = "Hemosorbsiya",
        ["51"] = "Terapiya",
        ["52"] = "Pediatriya",
        ["53"] = "Reanimasiya",
        ["100"] = "Təcili yardım",
        ["101"] = "Laboratoriya",
        ["102"] = "Nefrologiya",
        ["103"] = "Hematologiya",
        ["104"] = "Üz-çənə cərrahiyyəsi",
        ["105"] = "Toksikologiya",
        ["106"] = "Loqopediya",
        ["107"] = "Genetika",
        ["108"] = "Gerontologiya",
        ["109"] = "Dietologiya",
        ["110"] = "İmmunologiya",
        ["111"] = "Epidemiologiya",
        ["112"] = "Neonatologiya",
        ["113"] = "Ürək-damar cərrahiyyəsi",
        ["114"] = "Neyrocərrahiyyə",
        ["115"] = "Uşaq cərrahiyyəsi",
    };

    // http://fhir.az/CodeSystem/encounter-type -- terminology API'den cekildi (2026-08-19,
    // bkz. docs/sql-exports/cs_encounter-type.json). Sadece 1/2 kullaniliyoruz (bkz. asagida
    // typeCode hesaplamasi), digerleri (3=Evdə, 4=Sanatoriya, 5=Ambulator+Stasionar,
    // 6=konsultativ) su an mapper'da uretilmiyor.
    private static readonly Dictionary<string, string> EncounterTypeDisplay = new()
    {
        ["1"] = "Stasionar",
        ["2"] = "Ambulator",
    };

    // KARAR (2026-08-20, kullanici istegi): isim-bazli otomatik eslestirme + "Digər"(999)
    // fallback TERK EDILDI -- yanlis/belirsiz eslesme riski tasiyordu (orn. "Dermatologiya"
    // gibi tek-kelime isimler AZ listesindeki baska bir sey ile yanlislikla eslesebilirdi).
    // Artik SADECE Bolum Eslestirme sayfasindan elle, birebir eslestirilmis bolumler
    // gonderiliyor -- bkz. BolumMappingStore. Eslesmeyen/haric protokoller SKIPPED olur,
    // tahmini bir koda asla dusurulmez.
    // KULLANICI ISTEGI (2026-08-21): "reçete tipi protokolleri hiç göndermeyelim" --
    // kullanici Pusula ekranindan dogrulayarak ProtokolTipiId=31'in Reçete oldugunu
    // bildirdi. DB'deki Pusula.ProtokolTipi/Skrs.ProtokolTipi lookup tablolari BOS --
    // gercek isimler kullanicinin isaret ettigi Hasta.ProtokolTipi tablosunda bulundu
    // (2026-08-21). Bu tabloda DIKKAT: PK olan "Id" degil, ayri bir "ProtokolTipiId"
    // kolonu hasta.protokol.ProtokolTipiId ile eslesen gercek FK -- ayni ProtokolTipiId
    // icin yillar icinde birden fazla (cogu State=0, eski/iptal) satir bulunabiliyor,
    // asagidaki liste her biri icin EN GUVENILIR tek adi seciyor (once State=1, sonra en
    // son degistirilen -- SQL sorgusu konusmada mevcut). Sadece GORUNTULEME icin, gonderim
    // kurallari (bkz. ReceteProtokolTipiId asagida) buna bagli degil.
    public static readonly Dictionary<byte, string> ProtokolTipiDisplay = new()
    {
        [1] = "Genel Muayene",
        [2] = "Acil Muayene",
        [3] = "Kontrol (Muayene)",
        [4] = "Kontrol (Ameliyat Sonrası)",
        [5] = "Laboratuar",
        [6] = "Radyoloji",
        [7] = "Yatış Öncesi Ayaktan",
        [8] = "Donör",
        [9] = "Preop",
        [10] = "Ameliyat",
        [11] = "Medikal Tedavi",
        [12] = "Yoğun Bakım",
        [13] = "Acil Müşahede",
        [14] = "Müdahele",
        [15] = "Check-Up",
        [16] = "Acil Muayene (Yeşil Alan)",
        [17] = "FTR - Seans",
        [18] = "FTR - Seans Öncesi",
        [19] = "Kemoterapi",
        [20] = "Enjeksiyon",
        [21] = "Pansuman",
        [22] = "Kök Hücre Donörü",
        [23] = "Ameliyat (Doktor Referanslı)",
        [24] = "İkinci Görüş",
        [25] = "Online Muayene",
        [26] = "Endoskopi-Kolonoskopi",
        [27] = "Kemik İliği",
        [28] = "Organ Nakli",
        [29] = "IVF",
        [30] = "Konsey",
        [31] = "Reçete",
        [32] = "Diyaliz",
        [33] = "Refere",
        [34] = "ESWT",
        [92] = "Endoskopi - Kolonoskopi",
        [99] = "Diğer",
        [100] = "Doğum sonrası muayene (yenidoğan)",
        [101] = "Acil Muayene",
        [102] = "Acil Muayene Yeşil",
        [103] = "Acil Muayeneleri",
        [104] = "Acil Muayeneleri",
        [105] = "Acil Muayeneleri",
        [106] = "Diğer Hizmetler",
        [107] = "Check-Up/Sağlık Raporları",
        [108] = "Reçete",
        [109] = "Sgk Dış İstek",
        [110] = "Seçilmeyecek (Eski Program Hastası)",
        [111] = "Seans",
        [112] = "Tarama",
        [113] = "İşe Giriş",
        [114] = "Ehliyet Raporu",
        [115] = "Kontrol Muayenesi (Ücretli Hasta,İndirim Grubu)",
        [116] = "FTR - Seans Sonrası Muayene",
        [117] = "Ameliyat Öncesi Kontrol",
        [118] = "Acil Muayene (KIRMIZI ALAN)",
        [119] = "Acil Muayene (Branş)",
        [120] = "Girişimsel İşlemler",
        [121] = "deneme",
        [122] = "Radyoterapi",
        [123] = "Enjeksiyon(Poliklinik)",
        [124] = "Kontrol Muayene Eski Program",
        [125] = "Kampanya",
        [127] = "KONTROL MUYENESİ",
        [128] = "Kontrol Muayenesi",
        [129] = "Tetkik Kaydı",
        [130] = "Hekim Ön Görüşme",
        [131] = "İlaç Kaydı - Reçete",
        [132] = "Kontrol (Muayene) Eski Program",
        [133] = "Sarı Alan",
        [134] = "Acil Muayene (Sarı Alan)",
        [135] = "kontrol muayene (eski program)",
        [136] = "Kontrol Eski Muayene",
        [137] = "Kemik İliği",
        [138] = "Organ Nakli",
        [139] = "Onkoloji",
        [140] = "Endoskopi-Kolonoskopi",
        [141] = "IVF",
        [142] = "Saç Ekimi",
        [143] = "Kontrol (Muayene)",
        [144] = "Kontrol (Eski Program)",
        [145] = "İşyeri Hekimliği",
        [146] = "Acil Muayene (Sarı Alan)",
        [147] = "genel muayene1",
        [148] = "Acil Muayene (Sarı Alan)",
        [149] = "Çocuk Yoğun Bakım",
        [150] = "Yenidoğan Yoğun Bakım",
        [151] = "Genel Yoğun Bakım",
        [152] = "Evde Bakım",
        [153] = "Paket Devam Hizmeti",
        [154] = "ortodonti",
        [155] = "Genel Muayane",
    };

    public const byte ReceteProtokolTipiId = 31;

    // diagnosisConditionIds: KULLANICI ISTEGI (2026-08-24, bakanlik geri bildirimi --
    // "Encounter'a da tani ekleyin"): Encounter.diagnosis, AZ IG'de base FHIR'dan
    // degistirilmemis (bkz. StructureDefinition-az-encounter.json differential --
    // diagnosis hic gecmiyor), yani standart yapi (Reference(Condition)) gecerli.
    // Condition'lar bu Encounter BASARIYLA olusturulduktan/guncellendikten SONRA (gercek
    // AZ id'si bilindikten sonra) ayrica gonderiliyor -- bkz. EncounterSyncService -- bu
    // yuzden bu parametre burada NULL/bos ise alan hic eklenmez (ilk gonderimde
    // Condition'lar henuz yok).
    public static MappingResult Map(
        ProtokolListItem p, string azPatientId, string? azPractitionerId, IReadOnlyDictionary<int, string?> bolumMap,
        IReadOnlyList<string>? diagnosisConditionIds = null)
    {
        if (p.IsVoided)
            return new MappingResult.Skipped("Protokol Pusula'da iptal/silinmiş (State=0) -- gönderilmez");

        if (p.ProtokolTipiId == ReceteProtokolTipiId)
            return new MappingResult.Skipped("Protokol tipi Reçete -- bu tür protokoller e-Health'e gönderilmez");

        if (p.AcilisTarihi is null)
            return new MappingResult.Skipped("AcilisTarihi (period.start) eksik");

        var typeCode = p.GelisTipiId switch
        {
            "A" or "G" => "2", // Ambulator -- KARAR 2026-08-19: Gunubirlik de Ambulator sayilir
            "Y" => "1",        // Stasionar
            _ => null,
        };
        if (typeCode is null)
            return new MappingResult.Skipped($"Bilinmeyen GelisTipiId: {p.GelisTipiId ?? "(bos)"}");

        var classCode = p.GelisTipiId == "Y" ? "IMP" : "AMB"; // FHIR base v3-ActCode, AZ-spesifik degil

        if (p.BolumId is null || !bolumMap.TryGetValue(p.BolumId.Value, out var departmentCode) || string.IsNullOrWhiteSpace(departmentCode))
        {
            return new MappingResult.Skipped(
                $"Bölüm '{p.BolumAdi ?? "(bilinmiyor)"}' (Id={p.BolumId?.ToString() ?? "yok"}) için AZ eşleştirmesi henüz yapılmadı -- Bölüm Eşleştirme sayfasından eşleştirin");
        }

        var encounter = new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["id"] = $"encounter-{p.ProtokolId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-encounter" } },
            ["status"] = p.KapanisTarihi is not null ? "finished" : "in-progress",
            ["class"] = new JsonObject
            {
                ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode",
                ["code"] = classCode,
                ["display"] = classCode == "IMP" ? "inpatient encounter" : "ambulatory",
            },
            ["type"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["system"] = "http://fhir.az/CodeSystem/encounter-type",
                            ["code"] = typeCode,
                            ["display"] = EncounterTypeDisplay[typeCode],
                        },
                    },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["period"] = BuildPeriod(p.AcilisTarihi.Value, p.KapanisTarihi),
            // DUZELTME (2026-08-21, bakanlik geri bildirimi): coding'de sadece code/system
            // gonderilince bakanlik uygulamasinda (vatandas portali) bolum adi GORUNMUYORDU --
            // "Coding kisminda display alanini da gonderirseniz yansiyacaktir" dediler. Ayni
            // sorun olabilecek TUM coding'ler icin display eklendi (bkz. bu dosyada ve
            // PatientMapper/PractitionerMapper/CompositionMapper'da yapilan ayni duzeltme).
            ["serviceType"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    BuildDepartmentCoding(departmentCode),
                },
            },
            ["serviceProvider"] = new JsonObject
            {
                ["reference"] = "Organization/5204",
                ["display"] = "Liv Bona Dea",
            },
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = p.ProtokolId.ToString(),
                },
            },
        };

        if (azPractitionerId is not null)
        {
            encounter["participant"] = new JsonArray
            {
                new JsonObject { ["individual"] = new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" } },
            };
        }

        if (diagnosisConditionIds is { Count: > 0 })
        {
            // PusulaRepository.GetTanilarByProtokolIdAsync zaten birincil taniyi ONE
            // siraladigi icin dizideki sira = onem sirasi -- rank buna gore 1'den baslar.
            encounter["diagnosis"] = new JsonArray(diagnosisConditionIds
                .Select((conditionId, i) => (JsonNode)new JsonObject
                {
                    ["condition"] = new JsonObject { ["reference"] = $"Condition/{conditionId}" },
                    ["rank"] = i + 1,
                })
                .ToArray());
        }

        return new MappingResult.Success(encounter);
    }

    // HospitalDepartments'ta karsiligi olmayan bir kod gelirse (bolumMap'e elle, dictionary
    // disinda bir deger girilmis olabilir) GetValueOrDefault null doner -- bunu JsonObject'e
    // dogrudan atamak "display":null olarak GERCEKTEN gonderilmesine yol aciyordu (bkz. yukaridaki
    // DUZELTME notu). display alani sadece bilinen bir kod icin eklenir.
    private static JsonObject BuildDepartmentCoding(string departmentCode)
    {
        var coding = new JsonObject
        {
            ["system"] = "http://fhir.az/CodeSystem/hospital-departments",
            ["code"] = departmentCode,
        };
        if (HospitalDepartments.TryGetValue(departmentCode, out var display))
            coding["display"] = display;
        return coding;
    }

    private static JsonObject BuildPeriod(DateTime start, DateTime? end)
    {
        var period = new JsonObject { ["start"] = ToAzInstant(start) };
        if (end is not null)
            period["end"] = ToAzInstant(end.Value);
        return period;
    }

    // Pusula smalldatetime -> AZ FHIR ISO datetime+saat dilimi. Sunucu Baki saatiyle
    // (+04:00, DST yok) calisiyor kabul edilir -- Pusula.hasta.protokol de yerel saat.
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";

    // KARAR (2026-08-20): kapanmamis protokoller icin varsayilan davranis "kapanmasini
    // bekle, kapaninca gonder"; ama bircok Ayaktan (A) protokol hic formal kapanmiyor
    // (bkz. docs/encounter-mapping.md bolum 4 -- son 90 gunde A'larin %21'i acik, bunlarin
    // %21'i 90+ gundur acik). Bu yuzden Ayarlar sayfasindan yapilandirilabilir bir gun
    // esigi eklendi: protokol X gunden uzun suredir aciksa, kapanmayi beklemeden "gonderime
    // uygun" sayilir. Tek, tur-bagimsiz bir esik -- kullanici A/Y/G ayrimi istemedi.
    public static bool IsEligibleForSend(DateTime? acilisTarihi, DateTime? kapanisTarihi, int openProtokolSendAfterDays)
    {
        if (kapanisTarihi is not null) return true;
        if (acilisTarihi is null) return false;
        return acilisTarihi.Value <= DateTime.Now.AddDays(-openProtokolSendAfterDays);
    }
}
