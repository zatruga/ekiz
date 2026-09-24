using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PusulaEHealthSync.Export;

// Tek sayfalik minimal .xlsx uretici -- dis kutuphane YOK.
//
// NEDEN ELLE: projede Excel kutuphanesi yok ve tek bir dokum icin bagimlilik eklemek
// gereksiz. CSV de olurdu ama hastane bu dosyayi duzenleyip dolastiracak; gercek
// xlsx daha uygun.
//
// DIKKAT -- DAHA ONCE BU HATAYA DUSULDU (2026-09-15, "excel dosyasini acamiyorum"):
// CT_Worksheet semasinda alt elemanlarin SIRASI kesin: sheetPr, dimension, sheetViews,
// sheetFormatPr, cols, sheetData, ... <cols> KESINLIKLE <sheetData>'dan once ve
// <sheetViews>'dan SONRA gelmeli. Sira bozulursa Excel dosyayi "bozuk" diye acmiyor.
//
// Metinler inlineStr olarak yaziliyor -- sharedStrings.xml'e hic gerek kalmiyor,
// dosya biraz buyur ama uretim cok daha basit ve hatasiz.
public static class XlsxWriter
{
    public record Sutun(string Baslik, double Genislik, bool Sayi = false);

    /// <summary>Tek sayfalik xlsx uretir. Hucre degerleri string; Sayi=true olan sutunlar sayisal yazilir.</summary>
    public static byte[] Olustur(string sayfaAdi, IReadOnlyList<Sutun> sutunlar, IEnumerable<string?[]> satirlar)
    {
        var veri = satirlar.ToList();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Yaz(zip, "[Content_Types].xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);

            Yaz(zip, "_rels/.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);

            Yaz(zip, "xl/_rels/workbook.xml.rels",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);

            Yaz(zip, "xl/workbook.xml",
                $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{Kacir(sayfaAdi)}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);

            // 0: normal, 1: kalin baslik
            Yaz(zip, "xl/styles.xml",
                """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/><name val="Calibri"/></font></fonts>
                  <fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF0F6F66"/><bgColor indexed="64"/></patternFill></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="2">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>
                    <xf numFmtId="0" fontId="1" fillId="2" borderId="0" xfId="0" applyFont="1" applyFill="1"><alignment vertical="center" wrapText="1"/></xf>
                  </cellXfs>
                </styleSheet>
                """);

            Yaz(zip, "xl/worksheets/sheet1.xml", SayfaXml(sutunlar, veri));
        }
        return ms.ToArray();
    }

    private static string SayfaXml(IReadOnlyList<Sutun> sutunlar, List<string?[]> veri)
    {
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        var sonSutun = Harf(sutunlar.Count);
        sb.Append($"""<dimension ref="A1:{sonSutun}{veri.Count + 1}"/>""");

        // SIRA: sheetViews -> cols -> sheetData (bkz. sinif basindaki not)
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("""<sheetFormatPr defaultRowHeight="15"/>""");

        sb.Append("<cols>");
        for (var i = 0; i < sutunlar.Count; i++)
            sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{sutunlar[i].Genislik.ToString(System.Globalization.CultureInfo.InvariantCulture)}" customWidth="1"/>""");
        sb.Append("</cols>");

        sb.Append("<sheetData>");
        sb.Append("""<row r="1" ht="28" customHeight="1">""");
        for (var i = 0; i < sutunlar.Count; i++)
            sb.Append($"""<c r="{Harf(i + 1)}1" s="1" t="inlineStr"><is><t xml:space="preserve">{Kacir(sutunlar[i].Baslik)}</t></is></c>""");
        sb.Append("</row>");

        for (var satir = 0; satir < veri.Count; satir++)
        {
            sb.Append($"""<row r="{satir + 2}">""");
            for (var i = 0; i < sutunlar.Count && i < veri[satir].Length; i++)
            {
                var deger = veri[satir][i];
                if (string.IsNullOrEmpty(deger)) continue;
                var hucre = $"{Harf(i + 1)}{satir + 2}";
                if (sutunlar[i].Sayi && double.TryParse(deger, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var sayi))
                    sb.Append($"""<c r="{hucre}"><v>{sayi.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>""");
                else
                    sb.Append($"""<c r="{hucre}" t="inlineStr"><is><t xml:space="preserve">{Kacir(deger)}</t></is></c>""");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData>");
        sb.Append($"""<autoFilter ref="A1:{sonSutun}{veri.Count + 1}"/>""");
        sb.Append("</worksheet>");
        return sb.ToString();
    }

    private static string Harf(int n)
    {
        var s = "";
        while (n > 0) { n--; s = (char)('A' + n % 26) + s; n /= 26; }
        return s;
    }

    // XML'de gecersiz olan kontrol karakterleri de atiliyor -- Pusula'daki bazi
    // hizmet adlarinda bunlardan var ve Excel dosyayi acmiyor.
    private static string Kacir(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;
            sb.Append(ch switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&apos;", _ => ch.ToString() });
        }
        return sb.ToString();
    }

    private static void Yaz(ZipArchive zip, string yol, string icerik)
    {
        var giris = zip.CreateEntry(yol, CompressionLevel.Optimal);
        using var akis = giris.Open();
        using var yazici = new StreamWriter(akis, new UTF8Encoding(false));
        yazici.Write(icerik);
    }
}
