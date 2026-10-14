using System.Text.Json.Nodes;

namespace PusulaEHealthSync;

// Hasta.Hasta.EvAdresi -> AZ Patient.address (az-patient "residence" dilimi).
//
// BAKANLIK ISTEGI (2026-10-13): "Orneklerde adres ve medeni durum bilgilerine
// rastlamadik. Bu bilgiler kaynak sisteminizde mevcutsa ilgili FHIR alanlari
// uzerinden gonderime eklenmesi."
//
// KULLANICI KARARI (2026-10-14): "Evadresi sutununu kullanacagiz."
//
// ============================ KAYNAK NEYE BENZIYOR ============================
//
// EvAdresi SERBEST METIN ve icine ne gelirse geliyor. Olculdu (01.10 sonrasi
// protokolu olan 6.050 hasta, 6.028'inde alan dolu):
//
//   uzunluk 1-4   : 4.567  (%75,8)  -- sehir kisaltmasi, neredeyse hepsi "BAKI"
//   uzunluk 5-9   :   230
//   uzunluk 10-19 :   297
//   uzunluk 20-39 :   329
//   uzunluk 40+   :   680
//   tek kelime    : 4.792  (%79,5)
//
// Yani "alan %99,6 dolu" DOGRU ama YANILTICI: ortalama 12 karakter ve 6.028
// kayitta yalnizca 1.274 ayri deger var. Ayrica il/ilce id'leri (IlIdEv,
// IlceIdEv) ADA CEVRILEMIYOR -- Sube.Ortak_Il tablosu BOS ve IlIdEv=6385
// (5.994 hastada) hicbir tabloda karsilik bulmuyor.
//
// ============================ UC KATMAN ============================
//
// 1) UZUN ADRES -- 20+ karakter VE rakam iceriyor: 688 kayit (%11,4).
//    Bu alt kume gercekten adres: ortalama 60 karakter, %87'si virgulle
//    ayrilmis, 688'in 665'i benzersiz (tekrar eden etiket degil, gercek adres).
//    Bu kayitlar "text" olarak oldugu gibi gidiyor.
//
//    RAKAM SARTI KASITLI: kullanicinin ornek verdigi kayit (Hasta.Id=8311761)
//    42 karakter ama icerigi klavye karalamasi ("bakusdf sefdsdf sdf...") ve
//    RAKAM ICERMIYOR -- bu filtre onu disarida birakiyor. Ev/bina numarasi
//    olmayan uzun bir metin, adresten cok serbest not olma ihtimali tasiyor.
//
// 2) SEHIR -- degerin TAMAMI taninan bir sehir yazimiysa yalnizca "city".
//    Ayni sehir bes ayri bicimde yaziliyor (BAKI 3.339, BAKİ 915, baku 269,
//    BAKU AZERBAYCAN 49, bak; 22, BAKI SEH 6, BAKI SEHERI 6, bakı... 5,
//    BAKI AZERBAYCAN 5) -- hepsi "Bakı"ya normalize ediliyor.
//
// 3) RAYON -- Baki'nin rayonlari ("Xətai rayonu" vb.) "district" olur ve
//    sehir Baki yazilir.
//
// BUNLARIN DISINDA KALAN HIC GONDERILMIYOR: "UŞAQ EVİ" (bir kurum adi), iki
// harflik parcalar, karalama metinler. address 0..* oldugu icin bos birakmak
// profili bozmaz. AYNI ILKE AzPhone'da ve PathologyFindingMapper'daki
// morfoloji bicim denetiminde de uygulandi: uydurma ya da bozuk bir deger
// gondermek, hic gondermemekten kotudur.
//
// Kapsam: ~5.178 / 6.050 (%85,6) hastaya bir adres yaziliyor.
//
// ============================ PROFIL ============================
//
// az-patient, address'i SLICE ediyor (fhir.e-health.gov.az, 2026-10-14):
//   address:registered -> use=home, type=postal   (kayitli yasal adres)
//   address:residence  -> use=home, type=physical (mevcut ikamet adresi)
// use/type SERBEST DEGIL, dilime birebir uymali. Elimizdeki tek adres ev
// adresi oldugu icin yalnizca "residence" gonderiliyor; "registered" dilimi
// icin kaynak yok (KimlikIlId 13, KimlikIlceId 3, KimlikKoyMahalle 0 kayitta
// dolu -- fiilen bos). Alt alanlarda (city, district, text, country) ek kisit
// ya da zorunlu binding yok.
public static class AzAdres
{
    // ===== ANAHTAR: AZERBAYCAN/TURK HARFLERI ASCII'YE INDIRGENIR =====
    //
    // NEDEN: ilk yazimda sozluk StringComparer.OrdinalIgnoreCase kullaniyordu ve
    // CANLI VERIDE 915 KAYIT KACIYORDU. Kaynakta sehir "BAKİ" diye yaziliyor
    // (noktali buyuk I, U+0130) ve .NET'in ordinal buyuk/kucuk harf katlamasi
    // U+0130'u 'i'ye INDIRGEMEZ -- yani "BAKİ" ile "baki" ordinal olarak esit
    // degil. (Ayni veriyi SQL'de sayarken fark edilmedi: SQL Server'in Turkce
    // harmanlamasi LOWER('BAKİ') = 'bakı' yapiyor ve es gosteriyordu.)
    //
    // Bu yuzden hem sozluk anahtarlari hem gelen deger once ASCII'ye indirgeniyor;
    // sozlukteki anahtarlar da ASCII yazilmis durumda. Cikti degerleri ise DOGRU
    // Azerbaycanca yazimi koruyor ("Gəncə", "Xətai").
    private static string Anahtar(string ham)
    {
        var sb = new System.Text.StringBuilder(ham.Length);
        foreach (var c in ham)
        {
            sb.Append(c switch
            {
                'İ' or 'I' or 'ı' or 'i' => 'i',
                'Ə' or 'ə' => 'e',
                'Ş' or 'ş' => 's',
                'Ç' or 'ç' => 'c',
                'Ğ' or 'ğ' => 'g',
                'Ü' or 'ü' => 'u',
                'Ö' or 'ö' => 'o',
                _ => char.ToLowerInvariant(c),
            });
        }
        return sb.ToString();
    }

    // Degerin TAMAMI bu yazimlardan biriyse sehir olarak kabul edilir.
    // Anahtarlar ASCII'ye indirgenmis halde (bkz. Anahtar).
    private static readonly Dictionary<string, string> Sehirler = new(StringComparer.Ordinal)
    {
        ["baki"] = "Bakı",
        ["baku"] = "Bakı",
        ["bak;"] = "Bakı",              // yazim hatasi, 22 kayit
        ["baki..."] = "Bakı",
        ["baki seh"] = "Bakı",
        ["baki seheri"] = "Bakı",
        ["baku azerbaycan"] = "Bakı",
        ["baki azerbaycan"] = "Bakı",
        ["sumqayit"] = "Sumqayıt",
        ["gence"] = "Gəncə",
        ["masalli"] = "Masallı",
        ["semkir"] = "Şəmkir",
        ["berde"] = "Bərdə",
        ["xirdalan"] = "Xırdalan",
    };

    // Baki rayonlari -- "district" olur, sehir Baki yazilir.
    private static readonly Dictionary<string, string> Rayonlar = new(StringComparer.Ordinal)
    {
        ["xetai rayonu"] = "Xətai",
        ["yasamal rayonu"] = "Yasamal",
        ["bineqedi rayonu"] = "Binəqədi",
        ["nizami rayonu"] = "Nizami",
        ["nesimi rayonu"] = "Nəsimi",
        ["sabuncu rayonu"] = "Sabunçu",
        ["suraxani rayonu"] = "Suraxanı",
        ["narimanov rayonu"] = "Nərimanov",
    };

    private const string Ulke = "AZ";

    // Uzun adres esigi: bu uzunlugun altindaki serbest metin adres sayilmiyor.
    private const int UzunAdresEsigi = 20;

    // Gonderilecek bir adres cikmazsa null doner -- cagiran taraf alani hic
    // yazmaz (address 0..*).
    public static JsonObject? Coz(string? evAdresi)
    {
        var ham = AdSoyad.Temizle(evAdresi);
        if (ham is null) return null;

        // 2) ve 3): degerin TAMAMI taninan bir yer adiysa.
        var anahtar = Anahtar(ham);

        if (Sehirler.TryGetValue(anahtar, out var sehir))
            return Yap(text: null, city: sehir, district: null);

        if (Rayonlar.TryGetValue(anahtar, out var rayon))
            return Yap(text: null, city: "Bakı", district: rayon);

        // 1) Uzun ve rakamli -> gercek adres adayi.
        if (ham.Length >= UzunAdresEsigi && ham.Any(char.IsDigit))
        {
            // Metinde "bak" geciyorsa sehri de yaziyoruz (688 kaydin 481'inde
            // geciyor). Gecmiyorsa sehir BOS birakiliyor -- metinden sehir
            // TAHMIN ETMEK, yanlis sehir yazmak demek olabilirdi.
            var icindeBaki = anahtar.Contains("bak", StringComparison.Ordinal);
            return Yap(text: ham, city: icindeBaki ? "Bakı" : null, district: null);
        }

        // Kalan her sey: karalama metin, kurum adi, iki harflik parca -> hicbir sey.
        return null;
    }

    private static JsonObject Yap(string? text, string? city, string? district)
    {
        // az-patient "residence" dilimi: use ve type SABIT.
        var adres = new JsonObject
        {
            ["use"] = "home",
            ["type"] = "physical",
        };

        if (text is not null) adres["text"] = text;
        if (city is not null) adres["city"] = city;
        if (district is not null) adres["district"] = district;

        // Ulke yalnizca TANINAN bir yer adi cozuldugunde yaziliyor. Serbest
        // metin adreste sehir cozulemediyse hastanin Azerbaycan'da oldugu
        // iddia EDILMIYOR -- UlkeId Pusula'da neredeyse hep bos (ornek kayitta
        // da bos), yani dogrulayacak bir kaynagimiz yok.
        if (city is not null) adres["country"] = Ulke;

        return adres;
    }
}
