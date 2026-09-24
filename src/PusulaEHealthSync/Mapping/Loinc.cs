using System.Collections.Frozen;
using System.Reflection;
using System.Text.RegularExpressions;

namespace PusulaEHealthSync.Mapping;

// LOINC kod dogrulama + STANDART aciklama (display).
//
// BAKANLIK ISTEGI (2026-09-16, madde 7): "LOINC kodlarinda display alanina yalnizca
// 'CRP' veya 'PDW' gibi lokal kisa isimler yazilmamali; kodun standart aciklamasi
// kullanilmalidir." Ornekleri: 32207-3 -> "Platelet distribution width [Entitic
// volume] in Blood by Automated count", lokal ad ise code.text'e.
//
// KAYNAK SORUNU COZULDU (2026-09-23): AZ IG standart LOINC aciklamalarini YAYINLAMIYOR
// (az-lab-test-codes-vs dogrudan http://loinc.org'u filtresiz kapsiyor, expansion yok)
// ve bakanligin sandbox ucu terminoloji sorgusu kabul etmiyor -- canli denendi:
//   GET /fhir/CodeSystem/$lookup?system=http://loinc.org&code=32207-3
//     -> HTTP 400 "Unsupported resource type: CodeSystem. It is not in the allowed list."
//   GET /fhir/ValueSet/az-lab-test-codes-vs/$expand -> HTTP 404
// Aciklamalar bu yuzden HL7'nin RESMI acik terminoloji sunucusundan (tx.fhir.org,
// LOINC surum 2.82) cekilip projeye gomuldu -- AzIcd10 ile ayni kalip: calisma aninda
// internet gerekmesin, dis servis dustugunde gonderim durmasin.
//
// KONTROL BASAMAGI: LOINC kodunun son hanesi govdesinden Mod-10 (Luhn) ile hesaplanir.
// Bu, kodun GERCEKTEN LOINC olup olmadigini tabloya bakmadan soyleyebiliyor ve
// Pusula'daki sahte LOINC'lari yakaliyor: LIS.Test'te 101-1/101-2/101-3/101-4/101-5
// gibi diziler var -- ayni govdenin bes farkli kontrol basamagi olamaz, dogrusu 101-6.
// Olculdu (son 365 gun, 1.555.441 lab istemi): 14 kod / 5.825 istem sahte LOINC.
// Bunlari loinc.org sistemiyle gondermek, LOINC'ta olmayan bir kodu LOINC diye
// etiketlemek olurdu.
//
// GUNCELLEME: yeni test kodlari eklendiginde Resources/loinc-display.tsv yeniden
// uretilmeli (tx.fhir.org $lookup ile).
public static partial class Loinc
{
    private const string ResourceName = "PusulaEHealthSync.Resources.loinc-display.tsv";

    private static readonly Lazy<FrozenDictionary<string, string>> Tablo = new(() => Yukle(ResourceName));

    // TURKCE karsiliklar -- KULLANICI ISTEGI (2026-09-24): "onerilerin turkce
    // tercumelerinide listelesen guzel olur". Laboratuvar Ingilizce LOINC adini
    // okumak zorunda kalmasin diye. Yalnizca ONERDIGIMIZ kodlar icin var; eksikse
    // ekranda Ingilizce resmi ad gosterilir.
    private const string TrResourceName = "PusulaEHealthSync.Resources.loinc-tr.tsv";
    private static readonly Lazy<FrozenDictionary<string, string>> TrTablo = new(() => Yukle(TrResourceName));

    /// <summary>Kodun Turkce karsiligi; tanimli degilse null.</summary>
    public static string? DisplayTr(string? kod) =>
        string.IsNullOrWhiteSpace(kod) ? null
        : TrTablo.Value.TryGetValue(kod.Trim(), out var d) ? d : null;

    private static FrozenDictionary<string, string> Yukle(string kaynak)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(kaynak)
            ?? throw new InvalidOperationException($"Gomulu kaynak bulunamadi: {kaynak}");
        using var reader = new StreamReader(stream);

        var d = new Dictionary<string, string>(700, StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } satir)
        {
            var t = satir.IndexOf('\t');
            if (t <= 0) continue;
            d[satir[..t]] = satir[(t + 1)..];
        }
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public static int KodSayisi => Tablo.Value.Count;

    /// <summary>Kodun STANDART LOINC aciklamasi; tabloda yoksa null.</summary>
    public static string? Display(string? kod) =>
        string.IsNullOrWhiteSpace(kod) ? null
        : Tablo.Value.TryGetValue(kod.Trim(), out var d) ? d : null;

    // Tam LOINC bicimi: 1-5 hane + tire + tek kontrol basamagi.
    [GeneratedRegex(@"^(\d{1,5})-(\d)$")]
    private static partial Regex TamKod { get; }

    // LOINC + AYIRICILI yerel ek: 1533-9-A, 15045-8-SS, 882-1-Z7.
    // Ayirici VAR oldugu icin "taban LOINC + yerel varyant" tek makul okuma.
    [GeneratedRegex(@"^(\d{1,5})-(\d)[-.]([A-Za-z0-9]{1,3})$")]
    private static partial Regex AyiriciliEk { get; }

    // LOINC + BITISIK ek: 10842-3C (harf) veya 1558-62 (rakam).
    [GeneratedRegex(@"^(\d{1,5})-(\d)([A-Za-z0-9]{1,3})$")]
    private static partial Regex BitisikEk { get; }

    /// <summary>LOINC Mod-10 (Luhn) kontrol basamagi.</summary>
    public static int KontrolBasamagi(ReadOnlySpan<char> govde)
    {
        var toplam = 0;
        for (var i = 0; i < govde.Length; i++)
        {
            var d = govde[govde.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            toplam += d;
        }
        return (10 - toplam % 10) % 10;
    }

    private static bool Gecerli(Match m) =>
        KontrolBasamagi(m.Groups[1].ValueSpan) == m.Groups[2].Value[0] - '0';

    /// <summary>
    /// Pusula'daki test kodunu GERCEK LOINC koduna cozer. Cozemezse null doner --
    /// o test LOINC ile degil, az-other-lab-test-codes ile gonderilmeli.
    /// </summary>
    public static string? Coz(string? kod)
    {
        if (string.IsNullOrWhiteSpace(kod)) return null;
        var k = kod.Trim();

        if (TamKod.Match(k) is { Success: true } m && Gecerli(m))
            return k;

        // Ayiricili ek -> taban koda in (1533-9-A -> 1533-9). Olculdu: 51 kod,
        // 111.643 istem (son 365 gun) bu yolla kurtariliyor.
        if (AyiriciliEk.Match(k) is { Success: true } a && Gecerli(a))
            return $"{a.Groups[1].Value}-{a.Groups[2].Value}";

        // Bitisik ek: YALNIZCA HARF kabul ediliyor (10842-3C -> 10842-3).
        //
        // BITISIK RAKAM BILEREK DISARIDA (1558-62): iki turlu okunabiliyor --
        // "LOINC 1558-6 + yerel varyant 2" ya da dupeduz "1558-62" adli yerel kod.
        // Kontrol basamagi ikisini AYIRAMAZ, cunku 1558-6 zaten gecerli bir LOINC.
        // 26 kod / 92.816 istem; tahmin edip yanilirsak acliktan bakilan kan sekeri
        // olmayan bir testi bakanliga "acliktan glukoz" diye bildirmis oluruz.
        // Laboratuvar teyidi bekleniyor (bkz. Masaustu/lab-loinc-girilecek.xlsx,
        // "Teyit Gereken" sayfasi).
        if (BitisikEk.Match(k) is { Success: true } b && Gecerli(b) && !char.IsDigit(b.Groups[3].Value[0]))
            return $"{b.Groups[1].Value}-{b.Groups[2].Value}";

        return null;
    }
}
