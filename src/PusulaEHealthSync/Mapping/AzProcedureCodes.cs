using System.Collections.Frozen;
using System.Reflection;

namespace PusulaEHealthSync.Mapping;

// AZ hizmet/islem kodu -> STANDART Azerbaycanca aciklama (+ Turkce karsilik).
//
// KAYNAK: https://fhir.e-health.gov.az/CodeSystem-az-procedure-codes.json
// (content: complete, 6.094 kod, indirildi 2026-09-24). AzIcd10 ile ayni kalip --
// projeye gomuldu ki calisma aninda internet gerekmesin.
//
// NEDEN ONEMLI: Procedure.code bu listeye REQUIRED bagli. Ama ICD-10 ve LOINC'tan
// FARKLI olarak burada "kod var ama listede yok" sorunu YOK -- olculdu (son 365 gun):
// Icbari kodu olan 1.587 hizmetin HEPSI bu listede bulundu, sifir eksik. Sebebi
// Icbari Sigorta Fiyat Listesi'nin zaten bakanligin kendi listesi olmasi.
//
// TURKCE KARSILIKLAR: KULLANICI ISTEGI (2026-09-24) -- "bakanlik listesinde turkce
// cevirileri olsun". Hacme gore onceliklendirildi: kullanimdaki 1.066 kodun ilk
// 200'u hacmin %96,8'ini kapsiyor. Cevirisi olmayan kodda ekran Azerbaycanca
// orijinali gosterir.
public static class AzProcedureCodes
{
    private const string ResourceName = "PusulaEHealthSync.Resources.az-procedure-codes.tsv";
    private const string TrResourceName = "PusulaEHealthSync.Resources.az-procedure-tr.tsv";

    private static readonly Lazy<FrozenDictionary<string, string>> Tablo = new(() => Yukle(ResourceName));
    private static readonly Lazy<FrozenDictionary<string, string>> TrTablo = new(() => Yukle(TrResourceName));

    private static FrozenDictionary<string, string> Yukle(string kaynak)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(kaynak);
        if (stream is null) return FrozenDictionary<string, string>.Empty;
        using var reader = new StreamReader(stream);

        var d = new Dictionary<string, string>(6_500, StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } satir)
        {
            var t = satir.IndexOf('\t');
            if (t <= 0) continue;
            d[satir[..t]] = satir[(t + 1)..];
        }
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static int KodSayisi => Tablo.Value.Count;

    private static string Sadelestir(string kod) => kod.Trim().TrimEnd('.');

    /// <summary>Bakanlik listesindeki Azerbaycanca ad; kod listede yoksa null.</summary>
    public static string? Display(string? kod) =>
        string.IsNullOrWhiteSpace(kod) ? null
        : Tablo.Value.TryGetValue(Sadelestir(kod), out var d) ? d : null;

    /// <summary>Turkce karsilik; tanimli degilse null (ekran Azerbaycancaya duser).</summary>
    public static string? DisplayTr(string? kod) =>
        string.IsNullOrWhiteSpace(kod) ? null
        : TrTablo.Value.TryGetValue(Sadelestir(kod), out var d) ? d : null;

    /// <summary>Kod bakanlik listesinde var mi.</summary>
    public static bool Iceriyor(string? kod) =>
        !string.IsNullOrWhiteSpace(kod) && Tablo.Value.ContainsKey(Sadelestir(kod));
}
