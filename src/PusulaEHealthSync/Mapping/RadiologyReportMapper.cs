using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// RIS.TetkikIslem (RadiologyReportRecord) -> AZ DiagnosticReport FHIR resource (profile:
// az-radiology-diagnostic-report). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-radiology-diagnostic-report.json (2026-08-31).
//
// Zorunlu alanlar: status=final (sabit), category=RAD (sabit, HL7 v2-0074), code=LOINC
// 18748-4 "Diagnostic imaging study" (SABIT -- IG, Lab'in aksine, tetkikin ASIL turunu
// (rontgen/CT/MRI/hangi bolge) burada degil extension:related-procedure uzerinden baglanan
// Procedure.code'da tasiyor). Ucu extension zorunlu (min 3): local-system-unique-id (1..1),
// procedure-code (1..1, Lab/Islem ile AYNI Icbari koprusu), related-procedure (1..1,
// Reference(Procedure)).
//
// related-procedure -- ONEMLI BAGIMLILIK: Procedure.encounter (1..1) zorunlu oldugu icin
// bu Procedure'un AZ id'sinin ONCEDEN (EncounterSyncService.SyncProceduresAsync sirasinda)
// basariyla gonderilmis olmasi lazim -- ilgili Procedure hic gonderilmemis/basarisiz olmussa
// azProcedureId null gelir ve zorunlu alan doldurulamayacagi icin bu rapor Skipped olur
// (RadiologyReportSyncService cagiran taraf, ayni Encounter cagrisi icinde Procedure'lardan
// SONRA bu mapper'i cagirir).
public static class RadiologyReportMapper
{
    private const string CategorySystem = "http://terminology.hl7.org/CodeSystem/v2-0074";
    private const string ImagingStudyLoincSystem = "http://loinc.org";
    private const string ImagingStudyLoincCode = "18748-4";
    private const string ProcedureCodeExtensionUrl = "http://fhir.az/StructureDefinition/procedure-code";
    private const string ProcedureCodeSystem = "http://fhir.az/CodeSystem/az-procedure-codes";
    private const string RelatedProcedureExtensionUrl = "http://fhir.az/StructureDefinition/related-procedure";

    public static MappingResult Map(RadiologyReportRecord report, string azPatientId, string? azEncounterId, string? azProcedureId, string? azPractitionerId)
    {
        if (string.IsNullOrWhiteSpace(report.IcbariKodu))
            return new MappingResult.Skipped("İcbari Sigorta Fiyat Listesi eşleşmesi bulunamadı -- DiagnosticReport.extension:procedure-code zorunlu alanı doldurulamıyor, bu rapor gönderilemiyor");

        if (string.IsNullOrWhiteSpace(azProcedureId))
            return new MappingResult.Skipped("İlişkili tetkik işlemi (Procedure) e-Health'e henüz gönderilmedi -- DiagnosticReport.extension:related-procedure zorunlu alanı doldurulamıyor, bu rapor gönderilemiyor");

        if (string.IsNullOrWhiteSpace(report.Rapor))
            return new MappingResult.Skipped("Rapor metni boş -- gönderilecek içerik yok");

        var icbariKodu = report.IcbariKodu.TrimEnd('.');

        var diagnosticReport = new JsonObject
        {
            ["resourceType"] = "DiagnosticReport",
            ["id"] = $"diagnosticreport-{report.TetkikIslemId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-radiology-diagnostic-report" } },
            ["status"] = "final",
            ["category"] = new JsonArray
            {
                new JsonObject
                {
                    ["coding"] = new JsonArray
                    {
                        new JsonObject { ["system"] = CategorySystem, ["code"] = "RAD", ["display"] = "Radiology" },
                    },
                },
            },
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = ImagingStudyLoincSystem, ["code"] = ImagingStudyLoincCode, ["display"] = "Diagnostic imaging study" },
                },
            },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["conclusion"] = report.Rapor.Trim(),
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = "http://fhir.az/StructureDefinition/local-system-unique-id",
                    ["valueString"] = report.TetkikIslemId.ToString(),
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

        if (report.CalismaTarihi is not null)
            diagnosticReport["effectiveDateTime"] = ToAzInstant(report.CalismaTarihi.Value);

        if (report.OnaylanmaTarihi is not null)
            diagnosticReport["issued"] = ToAzInstant(report.OnaylanmaTarihi.Value);

        if (!string.IsNullOrWhiteSpace(azPractitionerId))
        {
            var performerRef = new JsonObject { ["reference"] = $"Practitioner/{azPractitionerId}" };
            diagnosticReport["performer"] = new JsonArray { performerRef.DeepClone() };
            diagnosticReport["resultsInterpreter"] = new JsonArray { performerRef };
        }

        return new MappingResult.Success(diagnosticReport);
    }

    // EncounterMapper/ProcedureMapper/LabResultObservationMapper ile ayni kural (Baki, +04:00, DST yok).
    private static string ToAzInstant(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";
}
