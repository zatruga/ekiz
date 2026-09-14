using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// RIS.TetkikIslem (RadiologyReportRecord) -> AZ ImagingStudy FHIR resource (profile:
// az-imaging-study). Kaynak: https://fhir.e-health.gov.az/
// StructureDefinition-az-imaging-study.json (IG'den okundu, 2026-09-11).
//
// KAPSAM: bu kaynak YALNIZCA META VERI tasir -- profilde `endpoint`, `Binary`,
// `Attachment` ya da WADO adresi YOK. Yani goruntulerin kendisi HICBIR ZAMAN
// gonderilmez (kullanici karari 2026-09-14: "radyoloji sadece study/imaj bilgisi,
// goruntu yok"). PACS'tan da yalnizca SORGU yetkisi isteniyor, indirme degil.
//
// Zorunlu alanlar: extension:local-system-unique-id (1..1), identifier (min 2 --
// ACSN ve UID slice'lari, ikisi de 1..1), modality (1..*), subject (1..1).
// Opsiyonel: encounter, numberOfSeries, numberOfInstances, series.
//
// STUDY INSTANCE UID DISARIDAN GELIR: Pusula'da yok, PACS'ta duruyor. Bos gelirse
// kaynak URETILMEZ (Skipped) -- uydurma bir DICOM UID gondermek, gercek calismayla
// hicbir zaman eslesmeyecek sahte bir kimlik yaymak olurdu. Ayni temkinli ilke:
// PathologyFindingMapper'daki morfoloji bicim denetimi.
public static class ImagingStudyMapper
{
    private const string LocalUniqueIdExtensionUrl = "http://fhir.az/StructureDefinition/local-system-unique-id";
    private const string IdentifierTypeSystem = "http://terminology.hl7.org/CodeSystem/v2-0203";
    private const string DicomUidSystem = "urn:dicom:uid";
    private const string DicomModalitySystem = "http://dicom.nema.org/resources/ontology/DCM";

    // DICOM UID bicimi: nokta ile ayrilmis sayi gruplari (orn. 1.2.840.113619.2.55...).
    private static readonly Regex DicomUidPattern = new(@"^\d+(\.\d+)+$", RegexOptions.Compiled);

    public static MappingResult Map(
        RadiologyReportRecord report,
        string accessionNumber,
        string? studyInstanceUid,
        string? modalityFromPacs,
        int? numberOfInstances,
        string azPatientId,
        string? azEncounterId)
    {
        if (string.IsNullOrWhiteSpace(accessionNumber))
            return new MappingResult.Skipped("Accession numarası üretilemedi -- identifier:ACSN zorunlu alanı doldurulamıyor");

        var uid = studyInstanceUid?.Trim();
        if (string.IsNullOrWhiteSpace(uid) || !DicomUidPattern.IsMatch(uid))
            return new MappingResult.Skipped(
                "PACS'tan Study Instance UID alınamadı -- identifier:UID zorunlu alanı doldurulamıyor. " +
                "Bu tetkik için ImagingStudy gönderilemez (radyoloji raporunun kendisi ayrıca gönderiliyor, " +
                "profilde ImagingStudy bağlantısı 0..* opsiyonel).");

        // PACS modalite dondurduyse o esas; dondurmediyse hizmet adindan turetilir.
        var modality = !string.IsNullOrWhiteSpace(modalityFromPacs)
            ? modalityFromPacs.Trim().ToUpperInvariant()
            : DeriveModality(report.HizmetAdi);
        if (modality is null)
            return new MappingResult.Skipped($"Modalite belirlenemedi (hizmet: \"{report.HizmetAdi}\") -- ImagingStudy.modality 1..* zorunlu");

        var imagingStudy = new JsonObject
        {
            ["resourceType"] = "ImagingStudy",
            ["id"] = $"imagingstudy-{report.TetkikIslemId}",
            ["meta"] = new JsonObject { ["profile"] = new JsonArray { "http://fhir.az/StructureDefinition/az-imaging-study" } },
            ["status"] = "available",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{azPatientId}" },
            ["identifier"] = new JsonArray
            {
                // ACSN -- Pusula'nin PACS'a gonderdigi numara ("BAK" + TetkikIslem.Id).
                Identifier("ACSN", "Accession ID", null, accessionNumber),
                // UID -- DICOM Study Instance UID, PACS'tan gelir.
                Identifier("UID", "Universal Identifier", DicomUidSystem, uid),
            },
            ["modality"] = new JsonArray
            {
                new JsonObject { ["system"] = DicomModalitySystem, ["code"] = modality },
            },
            ["extension"] = new JsonArray
            {
                new JsonObject
                {
                    ["url"] = LocalUniqueIdExtensionUrl,
                    ["valueString"] = LocalUniqueId(report.TetkikIslemId),
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(azEncounterId))
            imagingStudy["encounter"] = new JsonObject { ["reference"] = $"Encounter/{azEncounterId}" };

        var started = report.CalismaTarihi ?? report.OnaylanmaTarihi;
        if (started is not null)
            imagingStudy["started"] = AzTime.ToAzInstant(started.Value);

        if (numberOfInstances is > 0)
            imagingStudy["numberOfInstances"] = numberOfInstances.Value;

        return new MappingResult.Success(imagingStudy);
    }

    private static JsonObject Identifier(string typeCode, string typeDisplay, string? system, string value)
    {
        var id = new JsonObject
        {
            ["type"] = new JsonObject
            {
                ["coding"] = new JsonArray
                {
                    new JsonObject { ["system"] = IdentifierTypeSystem, ["code"] = typeCode, ["display"] = typeDisplay },
                },
            },
            ["value"] = value,
        };
        if (system is not null) id["system"] = system;
        return id;
    }

    // Pusula'da modalite kolonu VAR ama BOS (RIS.TetkikIslem.ModalityTipiId: 0/13.939
    // dolu, olculdu 2026-09-14 -- projedeki altinci "semada var ama hic doldurulmamis"
    // kolon). Bu yuzden hizmet adindan turetiliyor: Azerice hizmet adlari modaliteyi
    // tutarli bir onekle tasiyor ("KT, döş qəfəsi" = BT, "MRT, beyin" = MR, "USM,
    // tiroid" = US, "Ağciyər radioqrafiyası" = rontgen).
    //
    // KAPSAMA OLCULDU (son 90 gun, 13.872 onayli tetkik): %93,5 turetilebiliyor --
    // US 4.574, DX 3.462, CT 2.411, MR 2.279, MG 158, XA 84; 904 tetkik (%6,5)
    // turetilemiyor ve Skipped olur. PACS modalite dondurdugunde bu yola hic
    // girilmez, o yuzden asil cozum PACS erisimi.
    public static string? DeriveModality(string? hizmetAdi)
    {
        if (string.IsNullOrWhiteSpace(hizmetAdi)) return null;
        var a = hizmetAdi.ToLowerInvariant();

        if (a.StartsWith("kt,") || a.Contains("tomoqrafiya")) return "CT";
        if (a.StartsWith("mrt,") || a.Contains("mrt")) return "MR";
        if (a.StartsWith("usm,") || a.Contains("usm") || a.Contains("ultrasə") || a.Contains("exokardio")) return "US";
        if (a.Contains("mammoqrafiya")) return "MG";
        if (a.Contains("anqioqrafiya") || a.Contains("angioqrafiya")) return "XA";
        if (a.Contains("ssintiqrafiya") || a.Contains("szintiqrafiya")) return "NM";
        if (a.Contains("pet")) return "PT";
        if (a.Contains("radioqrafiya") || a.Contains("rentgen")) return "DX";
        return null;
    }

    // Pusula'nin PACS'a gonderdigi numaranin AYNISI: AccessionNoEkKarakter + TetkikIslem.Id
    // (canli veride dogrulandi 2026-09-11 -- HL7 ORM'deki OBR-2 "BAK393439" ve Pusula'nin
    // Radyolog PacsViewerLink ayari, iki bagimsiz kaynak ayni sonucu veriyor).
    public static string AccessionNumber(string? prefix, int tetkikIslemId)
        => $"{(prefix ?? "").Trim()}{tetkikIslemId}";

    // Radyoloji raporu (DiagnosticReport) ile ayni Pusula Id'sini paylasiyor ama AYRI bir
    // FHIR kaynagi -- onek olmadan FindExistingIdAsync karistirmaz cunku resourceType
    // farkli, yine de okunabilirlik icin ayristiriliyor.
    public static string LocalUniqueId(int tetkikIslemId) => $"imaging-{tetkikIslemId}";
}
