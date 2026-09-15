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
}
