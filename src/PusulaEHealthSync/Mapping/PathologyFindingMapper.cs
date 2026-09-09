using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// [EMR.Pathology].[EPulse] satiri (PathologyFindingRecord) -> AZ Observation FHIR resource
// (profile: az-pathology-finding). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-pathology-finding.json (IG'den okundu, 2026-09-08).
//
// Zorunlu alanlar: extension:local-system-unique-id (1..1), category (1..1, SABIT
// observation-category#laboratory -- DiagnosticReport'taki v2-0074#PAT ile KARISTIRMA,
// bunlar farkli iki CodeSystem), code (1..1, SABIT LOINC 59847-4 "Histology and Behavior
// ICD-O-3"), effective[x]:effectiveDateTime (1..1), component (2..*).
//
// component SLICE'lari -- ikisi de 1..1 ZORUNLU ve ikisi de REQUIRED binding:
//   morphology -> LOINC 59848-2, deger VS: icd-o-3-morphology-vs
//   topography -> LOINC 21855-2, deger VS: icd-o-3-topography-vs
// Iki VS de AYNI CodeSystem'i (http://fhir.az/CodeSystem/az-icd-o-3) suzuyor, sadece regex
// farkli: morfoloji "^[8-9].*", topografya "^C.*". Yani iki ekseni ayiran sey LOINC kodu.
//
// BICIMLER IG'den BIREBIR DOGRULANDI (2026-09-08): morfoloji "8500/3" (bolu isaretli),
// topografya "C50.9" (NOKTALI). Pusula'nin MorfolojiKoduCode alani zaten TAM bu bicimde --
// cevirim yok. Topografya ise SKRS ic kodu, bkz. PathologyTopographyMap.
//
// DISPLAY BILEREK YAZILMIYOR: AZ CodeSystem'indeki display'ler Azerice ("C50.9" =
// "Sud vezi, EGO") ve elimizde tam liste yok; yanlis bir display gondermek yerine
// CodeableConcept.text'e Pusula'nin kendi Turkce terimi yaziliyor -- text zaten tam olarak
// "kaynaktaki insan tarafindan okunabilir karsilik" icin var.
//
// KAPANDI (2026-09-08, canli $validate): az-lab-result-observation'da extension:procedure-code
// 1..1 ZORUNLU cikmisti (bkz. LabResultObservationMapper), ayni AZObservation tabanindan
// turedigi icin burada da istenebilir diye supheliydi -- ISTENMIYOR, procedure-code'suz
// govde HTTP 200 ile gecti. IG differential'i dogru soyluyormus.
//
// AMA DIKKAT -- $validate BURADA ZAYIF BIR GUVENCE: sunucu required binding'i DENETLEMIYOR.
// Olculdu (2026-09-08): component:topography = "C99.9" (var olmayan bir topografya) ve
// component:morphology = "9999/9" (uydurma) govdeleri de HTTP 200 aldi. Yani $validate
// YAPIYI dogruluyor, ANLAMI degil -- yanlis bir organ/tani kodu sessizce kabul edilir.
// Bu yuzden asagidaki eslestirme mantiginin dogrulugu tamamen bizde; tek gercek denetim
// noktasi docs/patoloji-topografya-icdo3-eslestirme.md tablosunun elle gozden gecirilmesi.
public static class PathologyFindingMapper
{
    private const string LoincSystem = "http://loinc.org";
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string IcdO3System = "http://fhir.az/CodeSystem/az-icd-o-3";
    private const string LocalUniqueIdExtensionUrl = "http://fhir.az/StructureDefinition/local-system-unique-id";

    private const string PathologyFindingLoincCode = "59847-4";
    private const string MorphologyLoincCode = "59848-2";
    private const string TopographyLoincCode = "21855-2";

    // ICD-O-3 morfoloji: 4 hane (8000-9999) + "/" + tek haneli davranis kodu.
    // ("8130/21" gibi 6 haneli varyant icin bkz. TryResolveMorphology -- derece eki ayiklanir.)
    //
    // DIKKAT -- burada SADECE BICIM denetleniyor, uyelik DEGIL. Olculdu (2026-09-09, AZ
    // CodeSystem'inin tam JSON'u indirilip karsilastirildi): hastanede kullanilan 359
    // morfoloji kodunun 18'i az-icd-o-3 (surum 0.1.1) listesinde YOK -- 104 satir / 60
    // rapor. Bunlar uydurma degil, WHO'nun daha yeni surumlerinde (ICD-O-3.2) tanimli
    // gercek kodlar (orn. 8509/3 invaziv solid papiller karsinom, 8380/2 EIN, 8507/3
    // invaziv mikropapiller); AZ listesi daha eski bir alt kume.
    //
    // KULLANICI KARARI (2026-09-09): bu kodlar YINE DE GONDERILIYOR. Gerekce: kodlar
    // gecerli ICD-O-3, sunucu kabul ediyor (required binding zaten denetlenmiyor -- bkz.
    // sinif yorumundaki $validate notu) ve alternatif, 60 gercek kanser bulgusunu hic
    // gondermemek olurdu. Bilincli olarak uyelik denetimi EKLENMEDI: AZ'nin 1137 morfoloji
    // kodunu repoya gomup her yeni WHO kodunu elle bakim etmek, cozdugunden cok sorun
    // uretirdi. Bakanlik listeyi guncellerse fark kendiliginden kapanir --
    // docs/bakanlik-sorulari.md'de acik soru olarak, tam 18'lik listesiyle duruyor.
    //
    // TOPOGRAFYA tarafinda boyle bir acik YOK: PathologyTopographyMap'teki 227 ICD-O-3
    // kodunun TAMAMI az-icd-o-3'te mevcut (ayni gun, ayni yontemle dogrulandi).
    private static readonly Regex IcdO3MorphologyPattern = new(@"^[89]\d{3}/\d$", RegexOptions.Compiled);

    // Ayni kod + yapisik derece hanesi ("8130/21"). 1. grup taban kod, 2. grup derece.
    private static readonly Regex IcdO3MorphologyWithGradePattern = new(@"^([89]\d{3}/\d)(\d)$", RegexOptions.Compiled);

    // Pusula'nin kendi tablosundan gelen degeri denetlemek icin (orn. "C50.9").
    private static readonly Regex IcdO3TopographyPattern = new(@"^C\d{2}\.\d$", RegexOptions.Compiled);

    // ICD-O-3'un "birincil bolge bilinmiyor" kodu. AZ CodeSystem'de var (dogrulandi
    // 2026-09-09: "Qeyri-muayyan birincili nahiya").
    private const string UnknownPrimarySiteCode = "C80.9";

    public static MappingResult Map(PathologyFindingRecord finding, string azPatientId, string? azEncounterId, string? azPractitionerId)
    {
        if (!TryResolveMorphology(finding.MorfolojiKoduCode, out var morphology, out var morphologyNote))
            return new MappingResult.Skipped($"Morfoloji kodu ICD-O-3 biçimine getirilemedi (\"{finding.MorfolojiKoduCode}\") -- component:morphology zorunlu alanı doldurulamıyor, bu bulgu gönderilemiyor");

        var topography = ResolveTopography(finding, out var topographyNote);
        var note = CombineNotes(morphologyNote, topographyNote);

        var observation = new JsonObject
        {
            ["resourceType"] = "Observation",
            ["id"] = $"observation-patoloji-bulgu-{finding.IslemReferansNumarasi}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-pathology-finding" } },
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
                    new JsonObject { ["system"] = LoincSystem, ["code"] = PathologyFindingLoincCode, ["display"] = "Histology and Behavior ICD-O-3" },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["effectiveDateTime"] = ToAzInstant(finding.RaporlamaZamani ?? finding.IstemZamani),
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = LocalUniqueIdExtensionUrl,
                    ["valueString"] = LocalUniqueId(finding.IslemReferansNumarasi),
                },
            },
            ["component"] = new JsonArray
            {
                Component(MorphologyLoincCode, "Morphology.ICD-O-3", "Morfologiya (XBT-O-3)", morphology, finding.MorfolojiKoduValue),
                Component(TopographyLoincCode, "Primary site Cancer", "Topoqrafiya (XBT-O-3)", topography, finding.YerlesimYeriValue),
            },
        };

        if (!string.IsNullOrWhiteSpace(azEncounterId))
            observation["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        if (!string.IsNullOrWhiteSpace(azPractitionerId))
            observation["performer"] = new JsonArray { new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" } };

        return new MappingResult.Success(observation, note);
    }

    // KULLANICI KARARI (2026-09-09): "eslestirmeyi zorlayalim, eslesmese de yine de
    // gonderelim". Onceden tabloda karsiligi olmayan yerlesim yeri kodu butun bulguyu
    // Skipped yapiyordu -- yani gercek bir kanser bulgusu, sirf organ kodunu ceviremedik
    // diye hic gonderilmiyordu. Artik UC KADEMELI cozuluyor ve son kademe her zaman bir
    // sonuc uretiyor:
    //
    //   1) Elle uretilip gozden gecirilen tablomuz (PathologyTopographyMap, 227 kod).
    //   2) Pusula'nin KENDI cevirici tablosu (Skrs.YerlesimYeri.TopografikKodu). Bugun
    //      bos, ama kullanici istegi: yeni kodlar cikarsa once Pusula'da denensin --
    //      hastane orayi doldurursa kod degisikligi olmadan cozulur. Oradan gelen deger
    //      de BICIM denetiminden geciyor; cop bir deger korlemesine iletilmez.
    //   3) C80.9 "Unknown primary site" (AZ CodeSystem'de mevcut, dogrulandi 2026-09-09:
    //      "Qeyri-muayyan birincili nahiya"). Bu UYDURMA bir kod DEGIL -- ICD-O-3'un tam
    //      da "birincil bolge bilinmiyor" demek icin ayirdigi resmi kodu. Yani bulguyu
    //      dusurmek yerine, bilmedigimizi DURUST bir sekilde soyluyoruz.
    //
    // 2. ve 3. kademeye dusuldugunde Note doldurulur -> SyncLogEntry.Message'a yansir,
    // boylece hangi kayitlarin tabloya eklenmesi gerektigi gunlukten gorulebilir.
    private static string ResolveTopography(PathologyFindingRecord finding, out string? note)
    {
        note = null;

        if (PathologyTopographyMap.TryGetIcdO3(finding.YerlesimYeriCode, out var mapped))
            return mapped;

        var fromPusula = finding.PusulaTopografikKodu?.Trim();
        if (!string.IsNullOrWhiteSpace(fromPusula) && IcdO3TopographyPattern.IsMatch(fromPusula))
        {
            note = $"Yerleşim yeri \"{finding.YerlesimYeriCode}\" ({finding.YerlesimYeriValue}) bizim eşleştirme tablomuzda yok; Pusula'nın kendi tablosundaki karşılık ({fromPusula}) kullanıldı. Tabloya eklenmeli: docs/patoloji-topografya-icdo3-eslestirme.md";
            return fromPusula;
        }

        note = $"Yerleşim yeri \"{finding.YerlesimYeriCode}\" ({finding.YerlesimYeriValue}) hiçbir eşleştirme tablosunda bulunamadı -- topografya \"{UnknownPrimarySiteCode}\" (bilinmeyen birincil bölge) olarak gönderildi. Doğru karşılık docs/patoloji-topografya-icdo3-eslestirme.md dosyasına eklenmeli.";
        return UnknownPrimarySiteCode;
    }

    // Morfoloji normalde oldugu gibi gecer. TEK istisna, ICD-O-3'un 6. hanesi olan
    // derece/grade ekinin koda yapisik geldigi durum ("8130/21", "8130/23" -- canli veride
    // 44 satir). Bu bir TAHMIN DEGIL, verinin kendisi dogruluyor: Pusula'daki terimler
    // "/21" icin "dusuk dereceli low grade", "/23" icin "yuksek dereceli high grade" diyor
    // -- yani 6. hane gercekten derece. Taban kod ("8130/2") ayiklanip kullaniliyor; derece
    // bilgisi ayri bir component gerektirdiginden (profilde zorunlu slice degil) simdilik
    // sadece Note'a yaziliyor.
    private static bool TryResolveMorphology(string? raw, out string morphology, out string? note)
    {
        morphology = "";
        note = null;
        var code = raw?.Trim();
        if (string.IsNullOrWhiteSpace(code)) return false;

        if (IcdO3MorphologyPattern.IsMatch(code)) { morphology = code; return true; }

        var withGrade = IcdO3MorphologyWithGradePattern.Match(code);
        if (withGrade.Success)
        {
            morphology = withGrade.Groups[1].Value;
            note = $"Morfoloji kodu \"{code}\" ICD-O-3'ün 6. hanesi (derece/grade: {withGrade.Groups[2].Value}) yapışık geldiği için taban kod \"{morphology}\" olarak gönderildi; derece bilgisi ayrıca iletilmedi.";
            return true;
        }

        return false;
    }

    private static string? CombineNotes(string? a, string? b)
        => a is null ? b : b is null ? a : $"{a} -- {b}";

    private static JsonObject Component(string loincCode, string loincDisplay, string codeText, string icdO3Code, string? sourceTerm)
    {
        var value = new JsonObject
        {
            ["coding"] = new JsonArray
            {
                new JsonObject { ["system"] = IcdO3System, ["code"] = icdO3Code },
            },
        };

        // Pusula'nin kendi Turkce terimi -- AZ display'i yerine text'e. Bkz. sinif yorumu.
        if (!string.IsNullOrWhiteSpace(sourceTerm))
            value["text"] = sourceTerm.Trim();

        return new JsonObject
        {
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = LoincSystem, ["code"] = loincCode, ["display"] = loincDisplay },
                },
                ["text"] = codeText,
            },
            ["valueCodeableConcept"] = value,
        };
    }

    // PathologyReportMapper/RadiologyReportMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => AzTime.ToAzInstant(dt);

    // "patoloji-bulgu-" oneki: Observation'a Laboratuvar sonuclari da gidiyor
    // (LabResultObservationMapper) ve iki ID uzayi BAGIMSIZ/ORTUSEN -- onek olmadan
    // FindExistingIdAsync yanlis kaydi bulup uzerine yazabilirdi. Ayni gerekce:
    // PathologyReportMapper.LocalUniqueId.
    public static string LocalUniqueId(int islemReferansNumarasi) => $"patoloji-bulgu-{islemReferansNumarasi}";
}
