using System.Globalization;
using System.Text.RegularExpressions;

namespace PusulaEHealthSync.Mapping;

// Tedavi.GenelMuayene.Bulgulari icindeki VITAL BULGULARI ayristirir.
//
// KULLANICI BILDIRDI (2026-09-18): "bu alanda doktorlar epikrizde bulgular alanina
// giriyor". Dogru cikti -- ONCEKI TESPITIM YANLISTI. Vital icin AYRILMIS uc ayri
// yapisal yer var ve UCU DE BOS:
//   Tedavi.YasamBulgusu                 -> 0 kayit
//   Tedavi.GenelMuayene kolonlari       -> Ates/KardiyakNabiz/TansiyonArter/SPO2/
//      SolunumSayisi/Boy/Kilo: 254.877 kaydin HICBIRINDE dolu degil
//   Aktarim.GenelMuayene                -> aktarim tablosu
// Veri, ekrandaki formun METNE SERILESTIRILMIS halinde Bulgulari icinde duruyor.
//
// BICIM (canli veriden, 2026-09-18):
//   "Boy:  170 cm   Kilo:  91 kg   Vücut Kitle İndeksi:  31.49   VYA:  2.02
//    Nabız (Dk):  77   KB-S (mmHg):  136   KB-D (mmHg):  85   SpO2:  98
//    Fizik Muayene Bulguları:  <serbest metin>"
// Etiket + ':' + iki bosluk + deger; alanlar arasi uc bosluk.
//
// ETIKETLER UC DILDE: ayni hastanede hekimler TR/AZ/EN arayuz kullaniyor.
// Ucunu birden tanimak ZORUNLU -- "Çəki" bilmeyen bir ayristirici 928 kilo olcumunu
// sessizce atlar (son 365 gun).
//
// Son 365 gunde 124.117 muayene kaydinda: Boy 46.013, Kilo 43.112, VKI 29.855,
// VYA 29.855, SpO2 16.891, KB-S 16.499, KB-D 16.427, Nabiz 16.268, Ates 6.713.
//
// SERBEST METNI AYRISTIRMIYORUZ: yalnizca ASAGIDAKI TAM etiketler, hemen ardindan
// ':' ve bir SAYI geldiginde kabul ediliyor; ustune deger makul araligin disindaysa
// atiliyor. "Fizik Muayene Bulguları" sonrasindaki klinik anlati bu iki sartla
// zaten elenmis oluyor, ayrica bir kesme isareti gerekmiyor (AZ arayuzde o basligin
// metni degisiyor, ona bel baglamak kirilgan olurdu).
public enum VitalTur
{
    Ates,
    Nabiz,
    Sistolik,
    Diastolik,
    Spo2,
    Boy,
    Kilo,
    Vki,
    Vya,
}

public readonly record struct VitalOlcum(VitalTur Tur, decimal Deger);

public static partial class VitalBulguParser
{
    // Etiket -> olcum. Sira onemli: uzun etiketler once denenmeli ki "Kilo" gibi kisa
    // bir etiket "Vücut Kitle İndeksi" icindeki bir parcayi yakalamasin.
    private static readonly (string Etiket, VitalTur Tur)[] Etiketler =
    [
        ("Vücut Kitle İndeksi", VitalTur.Vki),
        ("Bədən Kütlə İndeksi", VitalTur.Vki),
        ("Body Mass Index", VitalTur.Vki),
        ("Nabız (Dk)", VitalTur.Nabiz),
        ("Nəbz (Dəq)", VitalTur.Nabiz),
        ("KB-S (mmHg)", VitalTur.Sistolik),
        ("KB-D (mmHg)", VitalTur.Diastolik),
        ("Hərarət", VitalTur.Ates),
        ("Pyrexia", VitalTur.Ates),
        ("Ateş", VitalTur.Ates),
        ("SpO2", VitalTur.Spo2),
        ("Çəki", VitalTur.Kilo),
        ("Kilo", VitalTur.Kilo),
        ("Size", VitalTur.Boy),
        ("Boy", VitalTur.Boy),
        ("VYA", VitalTur.Vya),
        ("BSS", VitalTur.Vya),
        ("Kg", VitalTur.Kilo),
    ];

    // MAKUL ARALIKLAR -- serbest metinden gelen yanlis eslesmeleri eler ve acik veri
    // giris hatalarini (orn. ates 365) gondermeden keser. Genis tutuldu: amac klinik
    // dogrulama yapmak degil, SACMA degeri elemek.
    private static readonly Dictionary<VitalTur, (decimal Alt, decimal Ust)> Araliklar = new()
    {
        [VitalTur.Ates] = (30m, 45m),
        [VitalTur.Nabiz] = (20m, 250m),
        [VitalTur.Sistolik] = (50m, 300m),
        [VitalTur.Diastolik] = (20m, 200m),
        [VitalTur.Spo2] = (50m, 100m),
        [VitalTur.Boy] = (30m, 250m),
        [VitalTur.Kilo] = (1m, 400m),
        [VitalTur.Vki] = (5m, 100m),
        [VitalTur.Vya] = (0.2m, 4m),
    };

    // \b ONEMLI: "Baş-boyun:" icindeki "boyun" parcasi "Boy" etiketi sanilmasin diye.
    // Etiketten hemen sonra ':' ve bir sayi sart.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<etiket>Vücut Kitle İndeksi|Bədən Kütlə İndeksi|Body Mass Index|Nabız \(Dk\)|Nəbz \(Dəq\)|KB-S \(mmHg\)|KB-D \(mmHg\)|Hərarət|Pyrexia|Ateş|SpO2|Çəki|Kilo|Size|Boy|VYA|BSS|Kg)\s*:\s*(?<deger>-?\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OlcumPattern { get; }

    public static IReadOnlyList<VitalOlcum> Ayristir(string? bulgulari)
    {
        if (string.IsNullOrWhiteSpace(bulgulari)) return [];

        // Her tur icin ILK gecerli deger alinir. Ayni muayenede bir olcumun iki kez
        // gecmesi beklenmiyor; gecerse ilki form alani, sonraki serbest metindeki
        // tekrardir.
        var bulunan = new Dictionary<VitalTur, decimal>();

        foreach (Match m in OlcumPattern.Matches(bulgulari))
        {
            var etiket = m.Groups["etiket"].Value;
            var tur = TurBul(etiket);
            if (tur is null || bulunan.ContainsKey(tur.Value)) continue;

            var ham = m.Groups["deger"].Value.Replace(',', '.');
            if (!decimal.TryParse(ham, NumberStyles.Number, CultureInfo.InvariantCulture, out var deger)) continue;

            var (alt, ust) = Araliklar[tur.Value];
            if (deger < alt || deger > ust) continue;

            bulunan[tur.Value] = deger;
        }

        return [.. bulunan.Select(kv => new VitalOlcum(kv.Key, kv.Value))];
    }

    private static VitalTur? TurBul(string etiket)
    {
        foreach (var (e, tur) in Etiketler)
            if (string.Equals(e, etiket, StringComparison.OrdinalIgnoreCase))
                return tur;
        return null;
    }
}
