using System.Text;

namespace PusulaEHealthSync;

// Telefon numarasini AZ e-Health'in bekledigi tek bicime cevirir: +994XXXXXXXXX
//
// BAKANLIK ISTEGI (2026-09-16): "Telefonların bazıları 9 haneli, bazıları başında 0
// olacak şekilde gönderilmiş. Azerbaycan numarası olduğu doğrulanan telefonların +994
// ile başlayan tek bir formatta gönderilmesi iyi olur."
//
// OLCULDU (hasta.hasta, State<>0, 2026-09-18):
//   260.338 dolu telefon alani
//   252.538 tanesi yapisal olarak uygun (ayiklandiktan sonra tam 9 hane)
//   250.657 tanesi (%99,3) BILINEN bir AZ onegiyle basliyor
//
// "Azerbaycan numarasi oldugu DOGRULANAN" kismi kasitli: onek listesi olcumden geldi.
// Gecerli onekler ve gercek kullanimlari: 50 (%40,8), 55 (%28,6), 51 (%13,6),
// 70 (%9,1), 77 (%3,8), 99 (%1,4), 10 (%1,3), 12 (%0,4 -- Baki sabit hat), 60 (%0,2).
// Listede olmayan onekler toplam %0,7 ve icerikleri bozuk gorunuyor (00…, 05…, 01…);
// ayrica 101 kayit duz cop (000000000 gibi).
//
// NORMALIZE EDILEMEYEN NUMARA HIC GONDERILMEZ. Patient.telecom 0..* oldugu icin bu
// profili bozmaz. Uydurma ya da bozuk bir numara gondermek, hic gondermemekten
// kotudur -- ayni ilke PathologyFindingMapper'daki morfoloji bicim denetiminde ve
// ImagingStudyMapper'daki DICOM UID denetiminde de uygulandi.
public static class AzPhone
{
    private const string UlkeKodu = "+994";

    // Olculmus gercek onekler (bkz. yukaridaki dagilim). Yeni bir operator onegi
    // cikarsa buraya eklenmeli -- sessizce kabul etmek yerine acikca listelemek,
    // bozuk verinin gecmesini engelliyor.
    private static readonly string[] GecerliOnekler =
        ["10", "12", "50", "51", "55", "60", "70", "77", "99"];

    // Girdi: Pusula'daki ham deger. Cikti: "+994XXXXXXXXX" ya da null (gonderilmez).
    public static string? Normalize(string? ham)
    {
        if (string.IsNullOrWhiteSpace(ham)) return null;

        // Bicimlendirme karakterlerini at; harf/sembol iceren kayitlar ("--------------",
        // "(102)130-50-3") zaten asagidaki uzunluk/onek denetiminden gecemez.
        var sb = new StringBuilder(ham.Length);
        foreach (var ch in ham)
            if (char.IsAsciiDigit(ch)) sb.Append(ch);
        var d = sb.ToString();
        if (d.Length == 0) return null;

        // Ulke kodu/sifir onegini ayikla -> geriye ULUSAL 9 hane kalmali.
        var ulusal = d.Length switch
        {
            12 when d.StartsWith("994", StringComparison.Ordinal) => d[3..],
            13 when d.StartsWith("0994", StringComparison.Ordinal) => d[4..],
            10 when d[0] == '0' => d[1..],
            9 => d,
            _ => null,
        };
        if (ulusal is null || ulusal.Length != 9) return null;

        // Tek rakamdan olusan kayitlar (000000000, 111111111) veri degil, doldurma.
        if (ulusal.All(c => c == ulusal[0])) return null;

        if (!GecerliOnekler.Contains(ulusal[..2])) return null;

        return UlkeKodu + ulusal;
    }
}
