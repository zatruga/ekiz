using System.Net;
using System.Text.RegularExpressions;

namespace PusulaEHealthSync.Mapping;

// EMR.Pathology.Result.Document -- HTML-entity kodlu HTML iceriyor (canli veride dogrulandi,
// 2026-09-02: "&lt;b&gt;...&lt;/b&gt;" gibi -- bir zengin metin editorunden kaydedilmis, bazi
// kisimlari orn. "&amp;nbsp;" IKI KEZ escape edilmis). FHIR DiagnosticReport.conclusion duz
// metin (string) bekliyor, HTML etiketlerini oldugu gibi gondermek gecersiz olurdu.
// RtfText.ToPlainText'teki "sadece yeterince coz" felsefesiyle AYNI -- tam bir HTML parser
// degil, bu zengin metin editorunun urettigi sade html'i (b/strong/p/br/span/div, nbsp) duz
// metne cevirecek kadar.
public static partial class HtmlText
{
    [GeneratedRegex(@"</?(p|div|br|li|tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBoundaryRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    private const char NoBreakSpace = (char)0x00A0;

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Kaynak veride gozlemlendigi kadariyla en fazla iki katman escape var -- fazladan
        // decode zararli degil (zaten cozulmus metinde & karakteri kalmayacaktir).
        var decoded = WebUtility.HtmlDecode(html);
        decoded = WebUtility.HtmlDecode(decoded);

        var withBreaks = BlockBoundaryRegex().Replace(decoded, "\n");
        var stripped = AnyTagRegex().Replace(withBreaks, string.Empty);
        stripped = WebUtility.HtmlDecode(stripped); // etiket icindeki nitelik degerleri cozulurken aciga cikan ic ice entity'ler icin

        // &nbsp; decode edildikten sonraki bolunmez bosluk (NoBreakSpace) -- normal boslukla degistir.
        var text = stripped.Replace(NoBreakSpace, ' ').Replace("\r\n", "\n").Replace("\r", "\n");
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        var lines = text.Split('\n').Select(l => l.Trim());
        return string.Join('\n', lines).Trim();
    }
}
