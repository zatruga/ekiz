using System.Text.Json.Nodes;

namespace PusulaEHealthSync.Mapping;

// Pusula'daki LOKAL birim ifadesini UCUM koduna cevirir.
//
// BAKANLIK ISTEGI (2026-10-13): "Bazi kayitlarin code alaninda lokal birim
// ifadelerinin dogrudan kullanildigini gorduk. unit alaninda lokal gosterim
// korunabilir. Ancak code alaninda uygun UCUM karsiligi bulunmalidir."
// Verdikleri ornekler: sn -> s; K/uL -> 10*3/uL ("UCUM icerisinde buyuk K,
// kelvin anlamina gelir"); COI'nin kontrol edilmesi.
//
// ONCEKI HAL: LabResultObservationMapper lokal birimi hem unit hem code alanina
// AYNEN kopyaliyordu. Olculdu (01.10 sonrasi gonderilen 17.148 Observation'in
// govdelerinden, ust seviye + alt parametreler birlikte): 56 ayri birim ifadesi
// "UCUM kodu" diye gonderilmis.
//
// ============================ NEDEN BURADA ============================
//
// valueQuantity IKI YERDE kuruluyordu -- LabResultObservationMapper.ApplyValue
// (tek satirlik sonuc) ve .Group.cs (panel alt parametresi). Ikisi de ayni
// kurali elle tekrar yaziyordu. Bu projede ayni hesabin iki yerde durmasinin
// bedeli zaten odendi (Protokol Detay ile Aktivite 2026-09-15'te ayrismisti),
// bu yuzden deger uretimi tek yere alindi.
//
// ============================ UCUM'UN TUZAKLARI ============================
//
//   - BUYUK/KUCUK HARF ANLAMLIDIR: "fl" gecersiz, dogrusu "fL" (litre buyuk L).
//     Canli veride ikisi de var: fl 5.197, fL 1.779 -- ayni birim.
//   - "K" KELVIN demektir. Bin adet icin 10*3 yazilir (bakanligin uyarisi).
//   - MIKRO oneki ASCII "u"dur: ug, umol, uL. Kaynakta "µ" ve "mc" de geciyor.
//   - ULUSLARARASI UNITE koseli parantezlidir: [IU]. "IU/mL" gecersiz,
//     dogrusu "[IU]/mL".
//   - Birim OLMAYAN seyler koseli parantezle ya da annotation ile yazilir:
//     mmHg -> mm[Hg], pH -> [pH], HPF -> /[HPF].
//
// ============================ KARSILIGI OLMAYANLAR ============================
//
// Bazi ifadelerin UCUM karsiligi YOK: COI (cutoff index, 556 kayit), S/Co ve
// s\CO (sinyal/cutoff orani, 110), NTU (bulaniklik, 21), Index (9), GPL/MPL
// (fosfolipid unitesi, 4), unite (4).
//
// BUNLARA UCUM KODU UYDURULMUYOR. Kod yazilamiyorsa "code" VE "system" alanlari
// HIC yazilmiyor, yalnizca "unit" (lokal gosterim) gonderiliyor -- system'siz
// bir code anlamsiz, uydurma bir code ise duz yanlis olurdu. FHIR'da
// Quantity.code ve .system 0..1; birim-only bir Quantity gecerli.
// Ayni ilke AzPhone, AzFin ve PathologyFindingMapper'da da uygulandi.
//
// Bu alti ifade bakanliga soruldu (bkz. docs/bakanlik-sorulari.md).
public static class AzUcum
{
    private const string UcumSystem = "http://unitsofmeasure.org";

    // Lokal ifade -> UCUM kodu. Anahtarlar once birebir, bulunamazsa kucuk harfe
    // indirgenip aranir (bkz. Coz).
    //
    // ANAHTARLAR KIRPILMIS OLMALI: Coz gelen degeri once Trim() ediyor, bu yuzden
    // bastaki/sondaki bosluklu bir anahtar HICBIR ZAMAN eslesmez. Ilk yazimda
    // "µmol/L " anahtari boyle yazilmisti ve sessizce olu kaliyordu -- kapsam
    // dogrulamasi yakaladi.
    //
    // Sagdaki sayilar 01.10 sonrasi gonderim adedi -- hangi satirin ne kadar
    // onemli oldugu gorunsun diye.
    private static readonly Dictionary<string, string> Harita = new(StringComparer.Ordinal)
    {
        // --- zaten gecerli UCUM, kimlik eslemesi (degismeden gecsin diye burada) ---
        ["%"] = "%",                      // 18.169
        ["mmol/L"] = "mmol/L",            //  6.383
        ["g/dL"] = "g/dL",                //  3.845
        ["mg/dL"] = "mg/dL",              //  3.046
        ["U/L"] = "U/L",                  //  1.799
        ["pg"] = "pg",                    //  1.783
        ["fL"] = "fL",                    //  1.779
        ["ng/mL"] = "ng/mL",              //  1.047
        ["mg/L"] = "mg/L",                //    920
        ["pg/mL"] = "pg/mL",              //    347
        ["U/mL"] = "U/mL",                //     93
        ["mm"] = "mm",                    //    124
        ["mL"] = "mL",                    //     11
        ["mg/g"] = "mg/g",                //     20
        ["mmol/kg"] = "mmol/kg",          //    360
        ["s"] = "s",                      //      3
        ["nmol/L"] = "nmol/L",            //      1
        ["umol/L"] = "umol/L",            //      8
        ["ug/dL"] = "ug/dL",              //     66

        // --- buyuk/kucuk harf duzeltmesi (litre daima buyuk L) ---
        ["fl"] = "fL",                    //  5.197  en buyuk tek duzeltme
        ["ng/dl"] = "ng/dL",              //    142
        ["ng/ml"] = "ng/mL",              //     35
        ["pg/ml"] = "pg/mL",              //     12
        ["mg/dl"] = "mg/dL",              //      9
        ["nmol/l"] = "nmol/L",            //     22
        ["ug/dl"] = "ug/dL",              //      3
        ["ug/ml"] = "ug/mL",              //      5

        // --- bin/milyon adet: "K" kelvin demek, bakanligin uyarisi ---
        ["K/uL"] = "10*3/uL",             // 14.344
        ["M/uL"] = "10*6/uL",             //  1.783

        // --- birim olmayanlar: koseli parantez ---
        ["mmHg"] = "mm[Hg]",              //  3.596
        ["HPF"] = "/[HPF]",               //  1.575  "her buyuk buyutme alaninda"
        ["ph unites"] = "[pH]",           //    900

        // --- Pusula'daki yazim hatalari ---
        ["mmol\\L"] = "mmol/L",           //  1.080  ters boluyle yazilmis
        ["sn"] = "s",                     //    668  Turkce "saniye"
        ["İU/ml"] = "[IU]/mL",            //      8  Turkce noktali I ile
        ["µmol/L"] = "umol/L",            //      1  kaynakta sondaki bosluklu

        // --- mikro oneki: UCUM'da ASCII "u" ---
        ["µg/dl"] = "ug/dL",              //    140
        ["µg/mL"] = "ug/mL",              //     22
        ["µIU/ml"] = "u[IU]/mL",          //    344
        ["µU/ml"] = "uU/mL",              //     68
        ["mcg/L"] = "ug/L",               //      2

        // --- uluslararasi unite: [IU] ---
        ["IU/L"] = "[IU]/L",              //     24
        ["IU/ml"] = "[IU]/mL",            //     29
        ["IU/mL"] = "[IU]/mL",            //     18
        ["mIU/ml"] = "m[IU]/mL",          //     51

        // --- 24 saatlik toplama ---
        ["mg/24 saat"] = "mg/(24.h)",     //     10
        ["mmol/24 saat"] = "mmol/(24.h)", //      1

        // --- annotation: olculen sey parantez icinde ---
        ["U/gHb"] = "U/g{Hb}",            //      1
    };

    // Karsiligi OLMAYANLAR -- burada listelenmeleri kasitli: "unutulmus" ile
    // "bilerek birakilmis" ayirt edilebilsin diye. Bunlar icin code/system
    // yazilmiyor, yalnizca unit gidiyor.
    private static readonly HashSet<string> KarsiligiYok = new(StringComparer.OrdinalIgnoreCase)
    {
        "COI",          // 556 -- cutoff index
        "S/Co",         //  25 -- sinyal/cutoff orani
        "s\\CO",        //  85 -- ayni sey, ters boluyle
        "NTU",          //  21 -- nephelometric turbidity unit
        "Index",        //   9
        "GPL",          //   2 -- IgG fosfolipid unitesi
        "MPL",          //   2 -- IgM fosfolipid unitesi
        "unite",        //   4 -- hangi unite oldugu belirsiz
    };

    // Lokal birimin UCUM karsiligi; yoksa null.
    public static string? Coz(string? birim)
    {
        var ham = birim?.Trim();
        if (string.IsNullOrEmpty(ham)) return null;

        if (Harita.TryGetValue(ham, out var kod)) return kod;

        // Yazim farkliliklarini yakalamak icin kucuk harfle bir kez daha:
        // haritadaki anahtarlar da ayni bicimde karsilastiriliyor.
        foreach (var kv in Harita)
            if (string.Equals(kv.Key, ham, StringComparison.OrdinalIgnoreCase))
                return kv.Value;

        return null;
    }

    // Bilinmeyen bir birim mi, yoksa UCUM'da karsiligi OLMADIGI bilinen bir
    // birim mi? Ekranda/raporda ayirt edilebilmesi icin.
    public static bool KarsiligiYokBiliniyor(string? birim) =>
        birim is not null && KarsiligiYok.Contains(birim.Trim());

    // ===== valueQuantity -- TEK URETIM NOKTASI =====
    //
    // AZ profili valueQuantity kullanildiginda "unit"i ZORUNLU tutuyor (1..1):
    // 2026-08-29'da canli hata alindi, "Instance count for
    // 'Observation.value[x].unit' is 0". Bu yuzden birim YOKSA valueQuantity
    // hic kurulmuyor -- cagiran taraf valueString'e dusuyor.
    public static JsonObject? Deger(decimal sayi, string? birim)
    {
        var lokal = birim?.Trim();
        if (string.IsNullOrEmpty(lokal)) return null;

        var quantity = new JsonObject
        {
            ["value"] = sayi,
            // LOKAL GOSTERIM KORUNUYOR -- bakanlik acikca izin verdi:
            // "unit alaninda lokal gosterim korunabilir".
            ["unit"] = lokal,
        };

        // UCUM kodu ancak GERCEKTEN varsa yaziliyor; system de onunla birlikte.
        // Kod yoksa ikisi de yazilmiyor (system'siz code anlamsiz olurdu).
        if (Coz(lokal) is { } ucum)
        {
            quantity["system"] = UcumSystem;
            quantity["code"] = ucum;
        }

        return quantity;
    }
}
