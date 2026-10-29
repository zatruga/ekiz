using System.Text.RegularExpressions;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// HIZMET KURALLARI (2026-10-28, kullanici: "kafeterya hizmetleri kafeterya olarak basliyor,
// bu bizim bir kuralimiz").
//
// 1) ON EK KURALI: adi Ayarlar'daki sozcuklerle BASLAYAN hizmet kendiliginden "gonderilmez"
//    sayilir -- Hizmet Eslestirme'de elle isaretlemenin yaptigi her seyi yapar: gonderilmez,
//    tarama/Icbari kartina aday olmaz, eksik sayilmaz. Kural Pusula sorgusunda HIZMET ADINA
//    uygulanir (hizmet numarasina degil): sayfadaki katalog paketle gelen bir dosyadan
//    doluyor, Pusula'ya sonradan eklenen kafeterya kalemleri orada olmayabilir.
// 2) SOZCUK SUZGECI: adinda belirli sozcukler GECEN hizmetleri "tibbi olmayabilir" diye
//    listeler. Isaretleme YAPMAZ -- karar kullanicinin. TAM SOZCUK eslesir: "su" sozcugu
//    "suspenziya"yi yakalamasin.
//
// GUVENLIK: on ekler SQL'e literal olarak giriyor (QueryCandidatesAsync yalnizca tarih
// parametresi tasiyor). Bu yuzden Temizle yalnizca harf, rakam, bosluk, tire ve noktaya
// izin veriyor; LIKE'in ozel karakterleri ayrica kaciriliyor.
public static class HizmetKurali
{
    public static List<string> Ayristir(string? ham) =>
        (ham ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Temizle)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static string Temizle(string s) =>
        new string(s.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '.').ToArray()).Trim();

    public static async Task<List<string>> OnEkleriAsync(SettingsStore settings, CancellationToken ct) =>
        Ayristir(await settings.GetStringAsync(
            SettingsStore.HizmetGonderilmezOnEklerKey, SettingsStore.HizmetGonderilmezOnEklerDefault, ct));

    public static async Task<List<string>> SozcukleriAsync(SettingsStore settings, CancellationToken ct) =>
        Ayristir(await settings.GetStringAsync(
            SettingsStore.HizmetTibbiOlmayanSozcuklerKey, SettingsStore.HizmetTibbiOlmayanSozcuklerDefault, ct));

    public static bool OnEkUyar(string? ad, IReadOnlyCollection<string> onEkler) =>
        ad is not null && onEkler.Any(o => ad.TrimStart().StartsWith(o, StringComparison.CurrentCultureIgnoreCase));

    // " AND NOT (LTRIM(oh.Adi) LIKE N'Kafeterya%' OR ...)" -- on ek yoksa bos.
    public static string SqlHaric(IReadOnlyCollection<string>? onEkler, string kolon) =>
        onEkler is { Count: > 0 }
            ? " AND NOT (" + string.Join(" OR ", onEkler.Select(o => $"LTRIM({kolon}) LIKE N'{LikeKac(o)}%'")) + ")"
            : "";

    private static string LikeKac(string o) =>
        Temizle(o).Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]").Replace("'", "''");

    // Tam sozcuk: oncesinde ve sonrasinda harf/rakam olmamali. Bos listede null.
    public static Regex? SozcukDeseni(IReadOnlyCollection<string> sozcukler) =>
        sozcukler.Count == 0 ? null
            : new Regex(@"(?<![\p{L}\p{N}])(" + string.Join("|", sozcukler.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
