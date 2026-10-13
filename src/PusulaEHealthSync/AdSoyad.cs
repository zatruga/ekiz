namespace PusulaEHealthSync;

// Ad/soyad alanlarindaki bastaki-sondaki bosluklari temizler.
//
// BAKANLIK ISTEGI (2026-10-13): "Bir ornekte hekim adinin sonunda gereksiz bosluk
// bulundugunu gorduk. Practitioner/01a10195-7017-752c-80c0-6c77ac93591f --
// mevcut gonderim: "given": ["CABİR "], onerilen: "given": ["CABİR"]. Ad ve soyad
// alanlarinda gonderim oncesinde trim islemi uygulanmasi."
//
// OLCULDU (2026-10-13, 01.10 sonrasi):
//   DOKTOR  : 643 kaydin 19'unda bosluk var (12 ad, 4 hem ad hem soyad, 3 soyad)
//   HASTA   : 6.047 kaydin 17'sinde bosluk var (12 ad, 4 soyad, 1 her ikisi)
//
// BAKANLIK YALNIZCA DOKTORU GORDU, HASTADA DA VAR. Ayni kusur Patient.name'i de
// etkiliyor; ikisi birden temizleniyor.
//
// OLCUM TUZAGI -- ILK SAYIM "HEPSI TEMIZ" DEDI: SQL Server'da sondaki bosluklar
// string karsilastirmasinda YOK SAYILIR ('CABİR ' = 'CABİR' -> TRUE) ve LEN() de
// sondaki bosluğu saymaz. Dolayisiyla "Adi <> LTRIM(RTRIM(Adi))" testi sondaki
// bosluğu ASLA yakalamaz; 643 doktorun hepsi temiz goruntusu verdi. Dogru olcum
// DATALENGTH ile yapildi (bayt sayar, farki gorur). Bu alani tekrar olcen biri ayni
// tuzaga dusmesin diye buraya yaziliyor.
//
// NEDEN .NET Trim() SQL'den FAZLASINI YAKALAR: SQL'in LTRIM/RTRIM'i yalnizca ASCII
// bosluğu (0x20) kirpar; .NET Trim() tum Unicode bosluk karakterlerini kirpar --
// kirilmaz bosluk (U+00A0), sekme, satir sonu dahil. Yukaridaki 19/17 sayisi bu
// yuzden ALT SINIR; gercek sayi biraz daha yuksek olabilir.
//
// ICTEKI BOSLUK BILEREK KORUNUYOR: yalnizca bas ve son kirpiliyor. Cok parcali adlar
// ("ƏLİ RZA") gercek ve tek bosluklari anlamlidir; onlari sikistirmak uydurma bir
// duzeltme olurdu -- bakanlik da yalnizca bastaki/sondaki boslugu isaret etti.
public static class AdSoyad
{
    // Bos ya da yalnizca bosluktan ibaret deger null doner -- cagiran taraftaki
    // "eksikse gonderme" denetimleri (IsNullOrWhiteSpace) boylece dogru calisir ve
    // TRƏS'e bos string gitmez.
    public static string? Temizle(string? deger)
    {
        if (string.IsNullOrWhiteSpace(deger)) return null;
        return deger.Trim();
    }
}
