using System.Collections.Frozen;
using System.Reflection;

namespace PusulaEHealthSync.Mapping;

// AZ ICD-10 kod -> STANDART Azerbaycanca aciklama.
//
// BAKANLIK ISTEGI (2026-09-16): "Tanı açıklamalarının bazıları Türkçe, bazıları
// Azerbaycanca gönderilmiş. Yerel terminoloji servisindeki standart Azerbaycanca
// açıklamalarla uyumlu gönderilmesi gerekiyor."
//
// SEBEP: ConditionMapper display alanina Pusula'nin kendi ICD tablosundaki adi
// (Sube.Tedavi_ICD.Adi) yaziyordu ve o tablo KARISIK -- bir kismi Azerbaycanca, bir
// kismi Turkce ("Aterosklerotik kardiyovasküler hastalık", "EKLEMDE AĞRI, OMUZ
// BÖLGESİ"). Artik aciklama BAKANLIGIN KENDI listesinden okunuyor, yani birebir
// uyumlu.
//
// KAYNAK: https://fhir.e-health.gov.az/CodeSystem-az-icd-10.json (content: complete,
// 33.083 kod, indirildi 2026-09-16). Projeye kod<TAB>aciklama bicimine sadelestirilip
// gomulu kaynak olarak eklendi (~1,9 MB) -- calisma aninda internet erisimi gerekmesin,
// bakanligin sunucusu ulasilamaz oldugunda gonderim durmasin diye.
//
// GUNCELLEME: bakanlik listeyi guncellediginde Resources/az-icd-10.tsv yeniden
// uretilmeli (CodeSystem JSON'undan kod+display cikarilarak).
public static class AzIcd10
{
    private const string ResourceName = "PusulaEHealthSync.Resources.az-icd-10.tsv";

    private static readonly Lazy<FrozenDictionary<string, string>> Tablo = new(Yukle);

    private static FrozenDictionary<string, string> Yukle()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Gomulu kaynak bulunamadi: {ResourceName}");
        using var reader = new StreamReader(stream);

        var d = new Dictionary<string, string>(34_000, StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } satir)
        {
            var t = satir.IndexOf('\t');
            if (t <= 0) continue;
            d[satir[..t]] = satir[(t + 1)..];
        }
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static int KodSayisi => Tablo.Value.Count;

    // Kod listede yoksa null doner. Cagiran taraf Pusula'nin kendi metnine duser --
    // ama o kod zaten AZ ValueSet'inde olmadigi icin sunucu tarafindan reddedilecektir
    // (bkz. docs/bakanlik-sorulari.md, ICD-10 ValueSet boslugu maddesi).
    public static string? Display(string? kod) =>
        string.IsNullOrWhiteSpace(kod) ? null
        : Tablo.Value.TryGetValue(kod.Trim(), out var d) ? d
        : null;

    public static bool Iceriyor(string? kod) => Display(kod) is not null;
}
