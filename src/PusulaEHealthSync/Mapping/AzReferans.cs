using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PusulaEHealthSync.Mapping;

// Pusula'daki referans araligi METNINI FHIR referenceRange'e cevirir.
//
// BAKANLIK ISTEGI (2026-10-13): "Incelediğimiz örneklerde referans aralıkları
// yalnızca text içerisinde bulunuyor. Kaynak sistemde sayısal alt ve üst
// sınırlar mevcutsa referenceRange.low ve referenceRange.high alanlarının da
// birimleriyle birlikte gönderilmesini öneriyoruz."
//
// BAKANLIGIN KRITIK UYARISI -- TEK TARAFLI SINIRLAR:
//   "eGFR için >60 ifadesi doğrudan low: 60 olarak gönderilmemelidir. Çünkü
//    FHIR R4'te low: 60, 60 değerini de kapsar ve ≥60 anlamına gelir. Kaynak
//    anlamı doğrulanarak özgün ifadenin text alanında korunması uygundur."
//
// ============================ NEDEN METINDEN ============================
//
// Ilk planda LIS.TestNormalDeger tablosuna (SinirAlt/SinirUst float kolonlari)
// JOIN atmak dusunuldu. VAZGECILDI ve bu daha guvenli:
//
//   Referans araliklari YASA VE CINSIYETE gore degisir. Elimizdeki METIN,
//   laboratuvarin O HASTA icin uyguladigi kuralin SONUCU -- yani dogru satir
//   zaten secilmis durumda. Tabloya JOIN atmak, o kurali bizim yeniden
//   turetmemizi gerektirirdi ve yanlis satiri secmek klinik olarak yaniltici
//   olurdu. Yanlis referans araligi, hic referans araligi gondermemekten kotudur.
//
// ============================ OLCUM (01.10 sonrasi) ============================
//
// Gonderilen 71.565 referenceRange'in sekli:
//   68.835 (%96,2)  "a - b"            -> low/high AYRISTIRILABILIR
//    1.682 (%2,4)   tek tarafli (>60)  -> YAZILMAZ, metin korunur
//      906 (%1,3)   serbest metin      -> YAZILMAZ (orn. "NEGATİF")
//       94 (%0,1)   "- 0.25" gibi      -> YAZILMAZ (alt sinir bos, anlami belirsiz)
//       48 (%0,1)   cok satirli/kosullu-> YAZILMAZ (cinsiyet/faz bazli metinler)
//
// METIN HER ZAMAN GONDERILIYOR. Sinirlar ayristirilabilse de ayristirilamasa da
// ozgun ifade text alaninda duruyor -- bakanligin acik istegi.
public static class AzReferans
{
    // SIKI KALIP: bastan sona YALNIZCA "sayi - sayi". Icinde birim, not, ikinci
    // bir aralik ya da kosul gecen metinler KASITLI olarak disarida kaliyor --
    // "Qadın: Follikülar faza 3.03-8.08 mIU/mL" gibi cok kosullu bir metinden
    // tek bir aralik secmek, hangi kosulun gecerli oldugunu UYDURMAK olurdu.
    //
    // Tek tarafli ifadeler ("- 0.25", ">60", "<5") bu kalibi zaten gecemiyor:
    // isaretten sonra rakam gelmedigi icin ilk grup eslesmiyor.
    private static readonly Regex IkiTarafli = new(
        @"^\s*([-+]?\d+(?:[.,]\d+)?)\s*[-–—]\s*([-+]?\d+(?:[.,]\d+)?)\s*$",
        RegexOptions.Compiled);

    // Referans araligi dizisi; metin bossa null (alan hic yazilmaz).
    // birim: olcumun kendi birimi -- low/high ayni birimi tasir.
    public static JsonArray? Coz(string? referansMetni, string? birim)
    {
        var metin = referansMetni?.Trim();
        if (string.IsNullOrEmpty(metin)) return null;

        var aralik = new JsonObject();

        if (IkiTarafli.Match(metin) is { Success: true } m
            && Sayi(m.Groups[1].Value) is { } alt
            && Sayi(m.Groups[2].Value) is { } ust
            // TERS YAZILMIS ARALIK GONDERILMEZ: "10 - 3" bir veri hatasidir,
            // oldugu gibi gondermek FHIR'da anlamsiz bir aralik uretirdi.
            && alt <= ust)
        {
            aralik["low"] = AzUcum.BasitOlcu(alt, birim);
            aralik["high"] = AzUcum.BasitOlcu(ust, birim);
        }

        // OZGUN IFADE HER ZAMAN KORUNUYOR (bakanligin acik istegi). Sinirlar
        // yazilamadiginda tek tasiyici bu; yazildiginda da anlam kaybolmasin diye
        // yine duruyor ("0 - 60" ile "0 - 60 mg/dL" arasindaki fark gibi).
        aralik["text"] = metin;

        return new JsonArray { aralik };
    }

    // Pusula virgullu ondalik kullanabiliyor; AzUcum/LabResultObservationMapper
    // ile ayni normalizasyon.
    private static decimal? Sayi(string ham) =>
        decimal.TryParse(ham.Replace(',', '.'), NumberStyles.Float,
                         CultureInfo.InvariantCulture, out var d) ? d : null;
}
