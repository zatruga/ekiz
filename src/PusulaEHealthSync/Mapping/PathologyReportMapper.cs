using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// EMR.Pathology.Result (PathologyReportRecord) -> AZ DiagnosticReport FHIR resource (profile:
// az-pathology-diagnostic-report). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-pathology-diagnostic-report.json (2026-09-02).
//
// V1 KAPSAM KARARI (2026-09-02, kullanici ile birlikte): profil aslinda UC parcali bir
// zincir -- DiagnosticReport -> extension:composition (0..1, OPSIYONEL) -> Composition
// (az-pathology-report-composition) -> section.entry -> Observation (az-pathology-finding,
// ICD-O-3 morfoloji+topografya kodlu, min 2 component). ICD-O-3 kodlamasi icin Pusula
// tarafinda guvenilir bir kaynak/terminoloji eslestirmesi henuz YOK -- bu yuzden Radyoloji'deki
// AYNI karar tekrarlandi: v1 SADECE DiagnosticReport'u (serbest metin rapor + Icbari koprusu +
// related-procedure) kapsiyor, Composition/Finding/ICD-O-3 bilincli olarak ERTELENDI (composition
// extension'i 0..1 oldugu icin bu gecerli bir v1).
//
// Zorunlu alanlar: status=final (sabit), category=PAT (sabit, HL7 v2-0074), code=LOINC
// 11526-1 "Pathology study" (SABIT). Uc extension zorunlu (min 3): local-system-unique-id
// (1..1), procedure-code (1..1, Lab/Islem/Radyoloji ile AYNI Icbari koprusu), related-procedure
// (1..1, Reference(Procedure)) -- RadiologyReportMapper ile BIREBIR AYNI kalip.
public static class PathologyReportMapper
{
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/v2-0074";
    private const string PathologyStudyLoincSystem = "http://loinc.org";
    private const string PathologyStudyLoincCode = "11526-1";
    private const string ProcedureCodeExtensionUrl = "http://fhir.az/StructureDefinition/procedure-code";
    private const string ProcedureCodeSystem = "http://fhir.az/CodeSystem/az-procedure-codes";
    private const string RelatedProcedureExtensionUrl = "http://fhir.az/StructureDefinition/related-procedure";

    public static MappingResult Map(PathologyReportRecord report, string azPatientId, string? azEncounterId, string? azProcedureId, string? azPractitionerId)
    {
        if (string.IsNullOrWhiteSpace(report.IcbariKodu))
            return new MappingResult.Skipped("İcbari Sigorta Fiyat Listesi eşleşmesi bulunamadı -- DiagnosticReport.extension:procedure-code zorunlu alanı doldurulamıyor, bu rapor gönderilemiyor");

        if (string.IsNullOrWhiteSpace(azProcedureId))
            return new MappingResult.Skipped("İlişkili işlem (Procedure) e-Health'e henüz gönderilmedi -- DiagnosticReport.extension:related-procedure zorunlu alanı doldurulamıyor, bu rapor gönderilemiyor");

        var conclusion = HtmlText.ToPlainText(report.Document);
        if (string.IsNullOrWhiteSpace(conclusion))
            return new MappingResult.Skipped("Rapor metni boş -- gönderilecek içerik yok");

        // KULLANICI ISTEGI (2026-09-02): "sipariş notu" (henuz tam raporu yazilmamis, sadece
        // "X vakasi icin immunohistokimya calismasi yapilacak" diyen idari/on-bilgi kaydi)
        // GONDERILMESIN, sadece asil tani/bulgu iceren raporlar gitsin. Bu tur kayitlar
        // ReportState=4 ("Onaylanmis") olsa bile -- onay durumu ile icerigin gercek bir tani mi
        // yoksa idari not mu oldugu BAGIMSIZ (canli veride dogrulandi 2026-09-02: hem sipariş
        // notu hem gercek raporlar ayni ReportState=4'te). Ayrim SADECE metin uzunlugundan
        // yapilabiliyor -- canli ornekler: sipariş notlari ~239-240 karakter ("Açıklama
        // İmmunohistokimya çalışma... B-XXXXX-2026 nolu vakaya aid..."), gercek raporlar
        // 2000-2800+ karakter (KLİNİK MƏLUMAT/MAKROSKOPİYA/PATOLOJİ DİAQNOZ bolumleriyle). 400
        // esigi bu iki grup arasindaki genis (~10x) bosluga rahatca sigiyor.
        const int minRealReportLength = 400;
        if (conclusion.Length < minRealReportLength)
            return new MappingResult.Skipped($"Rapor metni çok kısa ({conclusion.Length} karakter) -- muhtemelen asıl tanı/bulgu değil, idari bir sipariş/ön-bilgi notu (örn. \"X vakası için immunohistokimya çalışması\"). Asıl rapor yazıldığında (Pusula'da güncellendiğinde) otomatik olarak gönderilebilir hale gelecek.");

        var icbariKodu = report.IcbariKodu.TrimEnd('.');

        var diagnosticReport = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["id"] = $"diagnosticreport-patoloji-{report.ResultId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-pathology-diagnostic-report" } },
            ["status"] = "final",
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = CategorySystem, ["code"] = "PAT", ["display"] = "Pathology" },
                    },
                },
            },
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = PathologyStudyLoincSystem, ["code"] = PathologyStudyLoincCode, ["display"] = "Pathology study" },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["conclusion"] = conclusion,
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = LocalUniqueId(report.ResultId),
                },
                new JsonObject
                {
                    ["url"] = ProcedureCodeExtensionUrl,
                    ["valueCodeableConcept"] = new JsonObject
                    {
                        ["coding"] = new JsonArray
                        {
                            new JsonObject { ["system"] = ProcedureCodeSystem, ["code"] = icbariKodu, ["display"] = report.IcbariAdi ?? icbariKodu },
                        },
                    },
                },
                new JsonObject
                {
                    ["url"] = RelatedProcedureExtensionUrl,
                    ["valueReference"] = new JsonObject { ["reference"] = $"Procedure/{azProcedureId}" },
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(azEncounterId))
            diagnosticReport["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        if (report.RequestedAt is not null)
            diagnosticReport["effectiveDateTime"] = ToAzInstant(report.RequestedAt.Value);

        if (report.ApprovedDate is not null)
            diagnosticReport["issued"] = ToAzInstant(report.ApprovedDate.Value);

        if (!string.IsNullOrWhiteSpace(azPractitionerId))
        {
            var performerRef = new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" };
            diagnosticReport["performer"] = new JsonArray { performerRef.DeepClone() };
            diagnosticReport["resultsInterpreter"] = new JsonArray { performerRef };
        }

        return new MappingResult.Success(diagnosticReport);
    }

    // EncounterMapper/ProcedureMapper/RadiologyReportMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";

    // ONEMLI: RIS.TetkikIslem.Id (Radyoloji) ile EMR.Pathology.Result.Id (Patoloji) BAGIMSIZ,
    // ORTUSEN iki ID uzayi -- ayni sayisal degeri paylasabilirler. Ikisi de e-Health'e AYNI
    // resourceType (DiagnosticReport) olarak gittigi icin, ciplak Id'yi local-system-unique-id
    // olarak kullanmak FindExistingIdAsync'in YANLIS kaydi bulup uzerine yazmasina (bir
    // radyoloji raporunun patoloji raporuyla karismasina) yol acabilirdi -- bu yuzden burada
    // "patoloji-" oneki ile ayristiriliyor (Radyoloji tarafi GERIYE DONUK UYUMLULUK icin
    // degistirilmedi, o zaten canlida ciplak TetkikIslemId ile kayitli).
    public static string LocalUniqueId(int resultId) => $"patoloji-{resultId}";
}
