using System.Text;

namespace PusulaEHealthSync.Mapping;

// Tedavi.GenelMuayene.Epikriz RTF olarak saklaniyor (canli veride dogrulandi, 2026-08-20:
// "{\rtf1..." ile basliyor -- muhtemelen bir RichTextBox kontrolunden kaydedilmis). FHIR
// Composition.section.text bir duz metin/XHTML narrative bekliyor, RTF kontrol kodlarini
// oldugu gibi gondermek hem gecersiz hem de bakanlik tarafinda okunamaz olurdu.
//
// Bu, RTF spesifikasyonunun TAMAMINI degil -- Word/RichTextBox'in urettigi SIRADAN klinik
// not RTF'lerini (kalin/italik, paragraf, sekme, Turkce/Azerice ozel karakterler) dogru
// cozecek kadarini uyguluyor. fonttbl/colortbl/stylesheet/pict gibi "icerik olmayan"
// gruplar tamamen atlanir.
public static class RtfText
{
    // codepage 1254 (Turkce) gibi legacy kod sayfalari .NET Core'da varsayilan olarak
    // KAYITLI DEGIL -- Encoding.GetEncoding(1254) bu kayit olmadan NotSupportedException
    // atar. Bir kez, tip ilk kullanildiginda kaydediliyor.
    static RtfText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static readonly HashSet<string> SkipDestinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "generator", "pict", "object",
        "footnote", "header", "footer", "headerl", "headerr", "headerf",
        "footerl", "footerr", "footerf", "listtable", "list", "listoverridetable",
        "listoverride", "rsidtbl", "xmlnstbl", "datastore", "themedata",
        "colorschememapping", "latentstyles", "revtbl", "nonshppict", "shp", "shpinst",
        "field", "fldinst", "bkmkstart", "bkmkend", "atnid", "atnauthor", "atndate",
    };

    public static string ToPlainText(string? rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return string.Empty;
        if (!rtf.TrimStart().StartsWith("{\\rtf"))
            return rtf.Trim(); // zaten duz metin -- oldugu gibi kullan

        var sb = new StringBuilder();
        var skipDepth = new Stack<bool>(); // her grup icin: bu grup (veya ustu) atlanmali mi
        var codepage = 1254; // Turkce Windows -- Pusula'nin varsayilan RTF ciktisi (dogrulanmadiysa fallback)
        var unicodeSkip = 1; // \ucN -- her \u sonrasi atlanacak ANSI fallback karakter sayisi
        var atGroupStart = false;
        var i = 0;
        var n = rtf.Length;
        var suppress = false; // aktif grup icerik olarak yazilmamali mi

        while (i < n)
        {
            var c = rtf[i];
            if (c == '{')
            {
                skipDepth.Push(suppress);
                atGroupStart = true;
                i++;
            }
            else if (c == '}')
            {
                suppress = skipDepth.Count > 0 && skipDepth.Pop();
                i++;
            }
            else if (c == '\\')
            {
                i++;
                if (i >= n) break;
                var ctrl = rtf[i];

                if (ctrl == '\'')
                {
                    // \'hh -- codepage'e gore tek bayt
                    i++;
                    if (i + 1 < n)
                    {
                        var hex = rtf.Substring(i, 2);
                        if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b) && !suppress)
                        {
                            try
                            {
                                var enc = Encoding.GetEncoding(codepage);
                                sb.Append(enc.GetString([b]));
                            }
                            catch { /* bilinmeyen codepage -- atla */ }
                        }
                        i += 2;
                    }
                    atGroupStart = false;
                    continue;
                }

                if (ctrl is '\\' or '{' or '}')
                {
                    if (!suppress) sb.Append(ctrl);
                    i++;
                    atGroupStart = false;
                    continue;
                }

                if (ctrl == '~') { if (!suppress) sb.Append(' '); i++; atGroupStart = false; continue; }
                if (ctrl == '-') { i++; atGroupStart = false; continue; }
                if (ctrl == '_') { if (!suppress) sb.Append('-'); i++; atGroupStart = false; continue; }

                if (!char.IsLetter(ctrl))
                {
                    // bilinmeyen kontrol sembolu -- tek karakter, parametresiz
                    i++;
                    atGroupStart = false;
                    continue;
                }

                // kontrol kelimesi: harfler + opsiyonel imzali sayisal parametre + opsiyonel tek bosluk
                var wordStart = i;
                while (i < n && char.IsLetter(rtf[i])) i++;
                var word = rtf.Substring(wordStart, i - wordStart);

                var paramStart = i;
                if (i < n && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                {
                    i++;
                    while (i < n && char.IsDigit(rtf[i])) i++;
                }
                var hasParam = i > paramStart;
                var param = hasParam ? int.Parse(rtf.Substring(paramStart, i - paramStart)) : (int?)null;

                if (i < n && rtf[i] == ' ') i++; // kontrol kelimesini sonlandiran tek bosluk yutulur

                if (atGroupStart && word.Equals("*", StringComparison.Ordinal))
                {
                    // \* zaten harf degil, buraya dusmez -- guvenlik icin birakildi
                }

                switch (word)
                {
                    case "ansicpg" when hasParam:
                        codepage = param!.Value;
                        break;
                    case "uc" when hasParam:
                        unicodeSkip = param!.Value;
                        break;
                    case "u" when hasParam:
                        if (!suppress)
                        {
                            var code = param!.Value;
                            if (code < 0) code += 65536;
                            sb.Append(char.ConvertFromUtf32(code));
                        }
                        // \u sonrasi \uc kadar ANSI fallback karakteri atla
                        for (var s = 0; s < unicodeSkip && i < n; s++)
                        {
                            if (rtf[i] == '\\' && i + 1 < n && rtf[i + 1] == '\'') { i += 4; }
                            else i++;
                        }
                        break;
                    case "par":
                    case "line":
                    case "row":
                        if (!suppress) sb.Append('\n');
                        break;
                    case "tab":
                        if (!suppress) sb.Append('\t');
                        break;
                    case "cell":
                        if (!suppress) sb.Append('\t');
                        break;
                    default:
                        if (SkipDestinations.Contains(word) && skipDepth.Count > 0)
                        {
                            // Bu grubun geri kalanini bastir. skipDepth'in tepesindeki eleman
                            // bu grup KAPANDIGINDA geri donulecek deger -- ona DOKUNMA, yoksa
                            // grup kapanista yanlislikla "suppress=true" olarak kalir ve
                            // bastirma disariya sizar (bu bug'in ilk hali tam olarak buydu).
                            suppress = true;
                        }
                        break;
                }

                atGroupStart = false;
            }
            else
            {
                if (!suppress) sb.Append(c);
                atGroupStart = false;
                i++;
            }
        }

        var text = sb.ToString();
        // RTF'nin kendi bosluk/satir sonu bicimlendirmesinden kalan fazlaliklari sadelestir.
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        var lines = text.Split('\n').Select(l => l.TrimEnd());
        return string.Join('\n', lines).Trim();
    }

    // Duz metni FHIR Narrative (section.text.div) icin guvenli XHTML'e cevirir --
    // paragraflar <p>, tek satir sonlari <br/>.
    public static string ToXhtmlDiv(string plainText)
    {
        var paragraphs = plainText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var body = new StringBuilder();
        foreach (var para in paragraphs)
        {
            var escapedLines = para.Split('\n').Select(Escape);
            body.Append("<p>").Append(string.Join("<br/>", escapedLines)).Append("</p>");
        }
        if (body.Length == 0) body.Append("<p></p>");
        return $"<div xmlns=\"http://www.w3.org/1999/xhtml\">{body}</div>";
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
