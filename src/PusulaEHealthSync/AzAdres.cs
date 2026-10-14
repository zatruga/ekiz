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
// 1) SERBEST METIN -- EvAdresi'nde ne yaziyorsa "text" olarak OLDUGU GIBI
//    gidiyor (kullanici karari 2026-10-14: "bu alanda yazan tum veriyi al ve
//    isle"). Tek istisna, degerin tamamen lookup'tan zaten gelen sehir/rayon
//    adi olmasi -- o zaman duz tekrar olurdu.
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

        // ===== Ortak.Il'den GELEN adlar (2026-10-14) =====
        // Lookup tablosu yazimi karisik tutuyor: "Bakü" (Turkce), "Ganja"/"Shaki"
        // (Ingilizce), "Sumqayit" (noktasiz). Bakanliga tutarli Azerbaycanca
        // yazimla gidiyor. Kullanimdaki 27 degerin Azerbaycan'da olanlari:
        ["ganja"] = "Gəncə",
        ["shaki"] = "Şəki",
        ["nakhchivan"] = "Naxçıvan",
        ["quba"] = "Quba",
        ["ismailli"] = "İsmayıllı",
        ["gedebey"] = "Gədəbəy",
        ["fizuli"] = "Füzuli",
        ["qabala"] = "Qəbələ",
        ["salyan"] = "Salyan",
        ["agdash"] = "Ağdaş",
        ["zaqatala"] = "Zaqatala",
        ["jalilabad"] = "Cəlilabad",
        ["shamkir"] = "Şəmkir",
        ["lankaran"] = "Lənkəran",
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

    // Gonderilecek bir adres cikmazsa null doner -- cagiran taraf alani hic
    // yazmaz (address 0..*).
    //
    // SIRA ONEMLI: sehir ONCE LOOKUP'tan (Ortak.Il) alinir, serbest metinden
    // DEGIL. Lookup yapilandirilmis veri ve %98,3 dolu; metinden sehir cikarmak
    // ancak lookup bos oldugunda basvurulan yedek yol.
    public static JsonObject? Coz(string? evAdresi, string? ilAdi = null, string? ilceAdi = null)
    {
        var ham = AdSoyad.Temizle(evAdresi);
        var il = AdSoyad.Temizle(ilAdi);
        var ilce = AdSoyad.Temizle(ilceAdi);

        // ---- 1) SEHIR: once lookup ----
        string? sehir = null;
        var azerbaycanda = false;
        if (il is not null)
        {
            if (Sehirler.TryGetValue(Anahtar(il), out var duzgun))
            {
                sehir = duzgun;          // taninan AZ sehri -> yazimi duzeltilir
                azerbaycanda = true;
            }
            else
            {
                // Taninmayan ad OLDUGU GIBI gider. Kullanimdaki 27 ilin 11'i
                // yurtdisi (Paris, Moscow, Riyadh, California, Tashkent...);
                // onlara "AZ" yazmak yanlis olurdu, bu yuzden ulke bos kalir.
                sehir = il;
            }
        }

        // ---- 2) ILCE: yalnizca SEHIRDEN FARKLIYSA ----
        // Canli veride IlceIdEv fiilen hep 3126 = "BAKÜ", yani ilin tekrari.
        // Ayni adi hem city hem district olarak gondermek gurultu olurdu.
        string? rayon = null;
        if (ilce is not null && (sehir is null || !Anahtar(ilce).Equals(Anahtar(sehir), StringComparison.Ordinal)))
        {
            rayon = Rayonlar.TryGetValue(Anahtar(ilce), out var r) ? r : ilce;
        }

        // ---- 3) SERBEST METIN ----
        //
        // KULLANICI KARARI (2026-10-14): "bu alanda yazan tum veriyi al ve isle."
        //
        // ILK YAZIMDA KALITE FILTRESI VARDI, KALDIRILDI. Kural "20+ karakter VE
        // rakam iceriyor" idi ve gerekcesi, kullanicinin ornek verdigi kayitta
        // (Id=8311761) gorulen klavye karalamasini ("bakusdf sefdsdf...")
        // bakanliga gondermemekti. GEREKCE ZAYIFTI: o filtrenin kac GERCEK adresi
        // dusurdugu hic olculmemisti. Olculdu (01.10 sonrasi): 20+ karakterli
        // 1.009 degerin 688'inde rakam var, yani 321 uzun deger yalnizca rakamsiz
        // oldugu icin eleniyordu -- "Yeni Yasamal qesebesi" gibi numarasiz ama
        // gayet gecerli adresler de o kumede.
        //
        // Kaynaktaki bozuk veri Pusula'nin veri kalitesi sorunudur ve GORUNUR
        // KALMALIDIR; onu burada maskelemek kullanici adina karar vermek olurdu.
        //
        // TEK ISTISNA -- YER ADININ TEKRARI: deger tamamen lookup'tan zaten gelen
        // sehir/rayon adiysa text olarak YAZILMIYOR. Bu bir kalite yargisi degil,
        // duz tekrar: ayni adi hem "city" hem "text" alaninda gondermek bilgi
        // eklemez. Canli veride bu 4.713 kaydi etkiliyor (hepsi "BAKI" varyanti).
        string? metin = null;
        if (ham is not null)
        {
            var anahtar = Anahtar(ham);
            var yalnizcaYerAdi = Sehirler.ContainsKey(anahtar) || Rayonlar.ContainsKey(anahtar);

            if (!yalnizcaYerAdi)
                metin = ham;

            // Lookup sehri vermediyse YEDEK YOL: metinden cozmeyi dene.
            if (sehir is null)
            {
                if (Sehirler.TryGetValue(anahtar, out var metinSehri))
                {
                    sehir = metinSehri;
                    azerbaycanda = true;
                }
                else if (Rayonlar.TryGetValue(anahtar, out var metinRayonu))
                {
                    sehir = "Bakı";
                    rayon ??= metinRayonu;
                    azerbaycanda = true;
                }
            }
        }

        // Hicbir parca cikmadiysa adres YAZILMAZ.
        if (metin is null && sehir is null && rayon is null) return null;

        return Yap(metin, sehir, rayon, azerbaycanda);
    }

    private static JsonObject Yap(string? text, string? city, string? district, bool azerbaycanda)
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

        // Ulke yalnizca TANINAN BIR AZERBAYCAN SEHRI cozuldugunde yaziliyor.
        // Kullanimdaki 27 ilin 11'i yurtdisi (Paris, Moscow, Riyadh, California,
        // Tashkent, Rostov...) -- onlara "AZ" yazmak duz yanlis olurdu. Hastanin
        // ulkesini dogrulayacak baska kaynak da yok: Hasta.UlkeId neredeyse hep
        // bos (kullanicinin ornek verdigi kayitta da bos).
        if (azerbaycanda) adres["country"] = Ulke;

        return adres;
    }
}
