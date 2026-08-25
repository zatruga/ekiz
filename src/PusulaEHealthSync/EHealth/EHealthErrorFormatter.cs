using System.Text.Json.Nodes;

namespace PusulaEHealthSync.EHealth;

// KULLANICI ISTEGI (2026-08-21, canli hatada bulundu -- composition-type $validate hatasi
// dashboard'da sadece "HTTP 400" olarak gorunuyordu, gercek neden -- hangi alanin, neden
// reddedildigi -- sadece ham ResponseJson'a inip bakarak anlasilabiliyordu): "tüm hatalarda
// detaylı açıklama yazmalı". e-Health sunucusu basarisiz istekte TEK BIR govde formati
// kullanmiyor -- standart FHIR OperationOutcome (issue[].diagnostics duz string), .NET FHIR
// API'nin kendi "value"-sarmali varyanti (issue[].details.text.value -- gorduk, 2026-08-21),
// ya da basit {"error": "..."} govdesi (orn. auth hatalari) donebiliyor. Bu sinif hepsini
// tarayip tek satirlik okunabilir bir ozet cikarir; hicbiri taniyamazsa ham govdeyi (kirpilmis)
// dondurur -- asla sessizce "HTTP 400" ile yetinmez.
public static class EHealthErrorFormatter
{
    public static string Describe(int statusCode, string? body)
    {
        var prefix = $"HTTP {statusCode}";
        if (string.IsNullOrWhiteSpace(body)) return prefix;

        var details = ExtractDetails(body);
        return string.IsNullOrWhiteSpace(details) ? prefix : $"{prefix}: {details}";
    }

    private static string? ExtractDetails(string body)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { return Truncate(body); }

        if (node?.AsObject() is not { } obj) return Truncate(body);

        if (obj["issue"]?.AsArray() is { Count: > 0 } issues)
        {
            var messages = issues
                .Select(i => DescribeIssue(i?.AsObject()))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
            if (messages.Count > 0) return string.Join(" | ", messages);
        }

        var simpleError = StringValue(obj["error"]) ?? StringValue(obj["message"]);
        if (!string.IsNullOrWhiteSpace(simpleError)) return simpleError;

        return Truncate(body);
    }

    private static string? DescribeIssue(JsonObject? issue)
    {
        if (issue is null) return null;
        var text = StringValue(issue["details"]?["text"]) ?? StringValue(issue["diagnostics"]);
        var location = issue["location"]?.AsArray()
            .Select(StringValue)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(text)) return location;
        return location is null ? text : $"{location}: {text}";
    }

    // .NET FHIR API govdesinde primitive alanlar bazen {"value": "..."} olarak sarilmis
    // geliyor (standart FHIR JSON'da duz deger olmasi gerekirken) -- her iki sekli de kabul et.
    private static string? StringValue(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v => v.ToString(),
        JsonObject o => StringValue(o["value"]),
        _ => null,
    };

    private static string Truncate(string body) => body.Length > 400 ? body[..400] + "..." : body;
}
