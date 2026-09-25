using System.Text.Json.Nodes;

namespace PusulaEHealthSync.Mapping;

// Gomulu tabloda OLMAYAN bir LOINC kodunun standart aciklamasini canli sorgular.
//
// NEDEN GEREKLI (kullanici bildirdi 2026-09-25): "yazilan kodun yada onaylanan
// kodun aciklamasi yazmasini istiyorum". Gomulu Resources/loinc-display.tsv
// yalnizca 929 kod tasiyor -- Pusula katalogunda gecenler + onerilerimiz + vital
// kodlari. LOINC'un tamami 100.000+ kod; laboratuvar bunlarin disinda GECERLI bir
// kod girdiginde ekran aciklama yerine "gomulu listede yok" uyarisi gosteriyordu.
//
// Sorgu HL7'nin resmi acik terminoloji sunucusuna gidiyor (tx.fhir.org) -- ayni
// kaynak gomulu tabloyu uretirken de kullanildi (bkz. Loinc.cs).
//
// IKI ISE YARIYOR:
//   1. Aciklama ekranda gorunur.
//   2. KOD DOGRULANIR -- yanlis yazilan kod aninda yakalanir, cunku sunucu
//      bulamazsa null doner.
//
// Sonuc cagiran tarafca veritabanina yazilir, boylece bir kod icin yalnizca BIR
// kez internete cikilir; sonrasinda internet olmasa da aciklama gorunur.
public class LoincLookup(HttpClient http, ILogger<LoincLookup> logger)
{
    private const string Sunucu = "https://tx.fhir.org/r4";

    /// <summary>
    /// Kodun standart LOINC aciklamasi. Once gomulu tabloya bakar, yoksa canli sorgular.
    /// Kod gecersizse ya da sunucuya ulasilamazsa null doner -- cagiran taraf bunu
    /// "dogrulanamadi" olarak gostermeli, HATA olarak degil (internet kesik olabilir).
    /// </summary>
    public async Task<string?> DisplayAsync(string? kod, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(kod)) return null;

        var gomulu = Loinc.Display(kod);
        if (gomulu is not null) return gomulu;

        var k = Uri.EscapeDataString(kod.Trim());
        var sistem = Uri.EscapeDataString("http://loinc.org");
        var adres = $"{Sunucu}/CodeSystem/$lookup?system={sistem}&code={k}";

        try
        {
            using var istek = new HttpRequestMessage(HttpMethod.Get, adres);
            istek.Headers.Accept.ParseAdd("application/fhir+json");
            using var yanit = await http.SendAsync(istek, ct);
            if (!yanit.IsSuccessStatusCode) return null;

            var govde = await yanit.Content.ReadAsStringAsync(ct);
            var parametreler = JsonNode.Parse(govde)?["parameter"]?.AsArray();
            if (parametreler is null) return null;

            foreach (var p in parametreler)
                if (p?["name"]?.GetValue<string>() == "display")
                    return p["valueString"]?.GetValue<string>();

            return null;
        }
        catch (Exception ex)
        {
            // Internet yoksa ya da sunucu dusukse eslestirme YINE DE kaydedilmeli --
            // aciklama sonradan tamamlanabilir.
            logger.LogInformation(ex, "LOINC {Kod} icin aciklama alinamadi (tx.fhir.org).", kod);
            return null;
        }
    }
}
