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

    // ================= UST KODA DUSME (2026-10-10) =================
    //
    // BAKANLIK DONUSU (kullanici aktardi, 2026-10-10): "eger ICD kodlarinda sistemde
    // eslesmeyen alt kirilimli kod var ise bunu UST KODU ile gonderebilirsiniz."
    //
    // OLCULDU (Pusula, 01.10 sonrasi): 780 farkli kod kullanilmis, 753'u listede var.
    // Kalan 27 kod (119 tani) reddediliyordu; bu kural 12 kodu (33 tani) kurtariyor.
    //
    // ---- KURAL NEDEN "SON KARAKTERI AT" DEGIL ----
    //
    // Naif bir kisaltma TIBBEN YANLIS TANI URETIYOR. Pusula'da ICD-10 alanina girilmis
    // bes ICD-O MORFOLOJI kodu var (M8960/3, M8800/3, M9122/0, M9392/3, M8170/3) --
    // bunlar tumor histolojisi kodlari, tani kodu degil. Son karakteri ata ata
    // gidilirse:
    //     M8960/3 (nefroblastom)  ->  M89  "Sümüklərin digər xəstəlikləri"
    //     M8170/3 (hepatosellüler ca) -> M81 "Patoloji sınıq olmadan osteoporoz"
    // Yani hastaya bambaska bir tani yazilmis olurdu. Bakanligin izni "alt kirilimi
    // ust kirilimla gonder" demek; "tanimadigin kodu benzeyen bir seye cevir" demek
    // DEGIL.
    //
    // Bu yuzden kural YAPISAL:
    //   1) Kod ICD-10 biciminde olmali: bir harf + iki rakam, istege bagli .rakamlar
    //   2) Yalnizca NOKTADAN SONRAKI kisim kisaltilir, teker teker
    //   3) Son durak uc karakterlik taban kod; tabanin kendisi ASLA kisaltilmaz
    //      ("M25" -> "M2" gibi bir sey uretilmez)
    // Bicime uymayan kod icin hic deneme yapilmaz -- oldugu gibi gider ve reddedilir,
    // ki dogrusu da bu: yanlis tani gondermektense gondermemek.
    //
    // ---- KURTARILAMAYANLAR ----
    // Kalan 15 kodun 5'i yukaridaki ICD-O kodlari. Digerleri gercek ICD-10 kodlari ama
    // bakanligin listesinde yoklar ve neredeyse hepsi YILDIZLI (*) kodlar -- G46, H19,
    // H36.0, H67, J91, M73, N74, G55.1. Yildizli kodlar ICD-10'da tek baslarina degil
    // hancer (+) koduyla birlikte kullanilir; AZ CodeSystem bunlari disariда birakmis
    // gorunuyor. Bu ayri bir bakanlik sorusu (bkz. docs/bakanlik-sorulari.md).
    private static readonly System.Text.RegularExpressions.Regex Icd10Bicimi =
        new(@"^[A-Z][0-9]{2}(\.[0-9]+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Gonderilecek kodu cozer. Kod listede varsa aynen doner. Yoksa ve ICD-10
    // biciminde bir ALT KIRILIM ise, listede bulunan ilk ust kod donulur.
    // Hicbiri olmazsa orijinal kod doner (gonderim denenir, sunucu reddeder --
    // kullanici Aktivite'de sebebini gorur).
    public static (string Kod, string? Display, bool UstKodaDusuldu) Coz(string? kod)
    {
        var k = (kod ?? "").Trim();
        if (k.Length == 0) return (k, null, false);

        if (Display(k) is { } d) return (k, d, false);
        if (!Icd10Bicimi.IsMatch(k.ToUpperInvariant())) return (k, null, false);

        var nokta = k.IndexOf('.');
        if (nokta <= 0) return (k, null, false);     // taban kod zaten; yukarisi yok

        var taban = k[..nokta];
        var ondalik = k[(nokta + 1)..];

        // En yakin ustten basla: "S62.60" -> "S62.6" -> "S62"
        for (var n = ondalik.Length - 1; n >= 1; n--)
        {
            var aday = $"{taban}.{ondalik[..n]}";
            if (Display(aday) is { } ad) return (aday, ad, true);
        }
        return Display(taban) is { } td ? (taban, td, true) : (k, null, false);
    }
}
