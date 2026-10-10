namespace PusulaEHealthSync;

// FIN (Vətəndaşın fərdi identifikasiya nömrəsi) normalizasyonu.
//
// KULLANICI KARARI (2026-09-15): "Pusula'da FIN numarasi kucuk yazilsa bile biz
// bunu hep BUYUK harf olarak kabul edelim."
//
// OLCULDU (2026-09-15, hasta.hasta, State<>0): 335.042 hastanin 11.725'i (%3,5)
// kucuk harfli FIN tasiyor, ayrica 456 anne FIN'i. Hepsi 7 karakter ve bicim
// olarak dogru -- tek sorun harf buyuklugu. AYRICA: ayni FIN'in hem buyuk hem
// kucuk yazildigi TEK BIR vaka bile yok, yani buyutmek iki ayri hastayi ayni
// kimlige tasiyamaz; temiz bir normalizasyon.
//
// NEDEN ToUpperInvariant, ToUpper DEGIL: uygulama Turkce kultur altinda calisiyor
// ve Turkcede 'i' harfinin buyugu 'I' DEGIL 'İ'dir (U+0130). FIN'lerin 97'sinde
// kucuk 'i' var (olculdu) -- ToUpper() ile bunlar 'İ' iceren, AZ FIN alfabesinde
// var olmayan bir degere donusur ve sunucu ya reddeder ya da YANLIS bir kimlikle
// kayit acar. Invariant kultur bu tuzagi ortadan kaldirir.
public static class AzFin
{
    // Bos/whitespace girdi aynen (null olarak) geri doner -- "FIN yok" durumunu
    // bos stringe cevirip asagidaki Skipped kontrollerini bozmamak icin.
    public static string? Normalize(string? fin)
    {
        if (string.IsNullOrWhiteSpace(fin)) return null;
        return fin.Trim().ToUpperInvariant();
    }

    // ================= BICIM DENETIMI (2026-10-10'da eklendi) =================
    //
    // KULLANICI: "hastanin FIN numarasi 7 haneli, neden bu hatayi veriyor?"
    //
    // Deger "GFF3ÜED" idi -- 7 KARAKTER, ama 'Ü' ASCII degil. Bakanligin
    // az-fin-format kisiti yalnizca ASCII harf/rakam kabul ediyor, bu yuzden
    // reddediyordu. Biz yalnizca BUYUK HARFE ceviriyorduk, karakter kumesine
    // hic bakmiyorduk; deger oldugu gibi gidiyor, HTTP 400 aliyor, sonra
    // "hatali" diye 3 kez daha deneniyordu.
    //
    // OLCULDU (Pusula, 01.10 sonrasi protokolu olan hastalar): 4.394 FIN gecerli,
    // 333 bos, 57 yedi karakter degil, 16 ASCII disi karakter tasiyor. Gecersiz
    // karakterlerin tamami Turkce klavye karsiliklari:
    //     İ (U+0130) x10,  Ü x7,  Ç x2,  Ş x1,  _ x1
    //
    // CEVIRI (transliterasyon) BILEREK YAPILMIYOR. 'İ' -> 'I' cevirisi makul
    // GORUNUYOR ama bir KIMLIK NUMARASINI tahmin etmek demek; Azerbaycan
    // alfabesinde I ve İ AYRI harfler ve yanlis tahmin, hastayi bakanlik
    // kayitlarinda baska biri olarak acar. Duzeltme Pusula'da yapilmali --
    // burada yalnizca TESPIT edilip sebebi soyleniyor.
    //
    // SONUC "Skipped", "Failed" DEGIL: bu veri duzeltilmeden hicbir zaman
    // gitmez, dolayisiyla tekrar denemek bosa ag trafigi. Skipped olan kayit
    // Takilanlar listesine dusuyor ve orada sebebiyle gorunuyor.
    public static bool Gecerli(string? fin) =>
        fin is { Length: 7 } && fin.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z');

    // Gecersizse NEDEN gecersiz -- ekranda gosterilecek Turkce aciklama; gecerliyse null.
    public static string? BicimHatasi(string? fin)
    {
        if (string.IsNullOrWhiteSpace(fin)) return null;   // "bos" ayri bir durum, cagiran taraf bakiyor
        if (fin.Length != 7)
            return $"FIN {fin.Length} karakter, TRƏS 7 karakter bekliyor -- Pusula'daki kimlik numarası düzeltilmeli.";

        var kotu = fin.Where(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'Z')).Distinct().ToArray();
        if (kotu.Length == 0) return null;

        // Hangi karakterin sorun oldugunu YAZIYORUZ -- "bicim hatali" tek basina
        // kullaniciya Pusula'da neyi duzeltecegini soylemiyor.
        var liste = string.Join(", ", kotu.Select(c => $"'{c}'"));
        // "Turkce klavye" ipucu YALNIZCA HARFLERDE: Ü/İ/Ç/Ş icin dogru bir tahmin,
        // ama '_' ya da bosluk icin yaniltici olurdu -- orada sebep baska.
        var ipucu = kotu.All(char.IsLetter)
            ? " -- büyük olasılıkla Türkçe klavyeyle girilmiş"
            : "";
        return $"FIN 7 karakter ama {liste} karakteri AZ FIN alfabesinde yok "
             + $"(yalnızca A-Z ve 0-9 kabul ediliyor){ipucu}. "
             + "Pusula'daki kimlik numarası düzeltilmeli.";
    }
}
