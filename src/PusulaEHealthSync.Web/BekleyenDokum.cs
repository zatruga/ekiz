using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web;

// ===== BEKLEYEN ISLER -- SEBEP DOKUMU (2026-10-27) =====
//
// KULLANICI: "cok fazla sayi var, insan hicbir sey anlamiyor ... gonderim kurallarina gore
// bekleyenleri saysin ... neden protokollerin gonderime uygun olmadigini buradan goreyim,
// lab sonucu cikmayan, rad raporu yazilmayan ... yapilmasi gereken oneriler". Tasarim
// tuvalinde A (akis) ile C (protokol sorgusu) secildi.
//
// Tarama sonucunu DORT ASAMAYA boler:
//   1 Hazir            -- uygun, sirasini bekliyor
//   2 Kural geregi     -- ayni gun / acik protokol / yatan taburcu olmadi ... sure dolunca gider
//   3 Pusula'da is     -- onay, epikriz, sonuc degeri: biri isini bitirince kendiliginden gider
//   4 Veri duzeltilmeli-- FIN, Icbari eslesmesi, LOINC: tanim duzeltilmezse HIC gitmez
//
// 1-2 ana (bekleyen) kumeden PROTOKOL basina, 3-4 takilan kumeden KALEM basina
// siniflandirilir -- takili bir protokolun kalemleri farkli sebeplerden takilmis olabilir
// (epikriz bos + bir tetkikin LOINC'i yok). Bir protokol birden fazla satirda gorunebilir.
//
// SAF HESAP: Pusula'ya ya da SyncLog'a gitmez, yalnizca PendingWorkService'in son
// sonucunu okur. Sebep kurallari PendingWorkService.Siniflandir/IsEligible'in metinlerine
// dayanir; orada bir metin degisirse buradaki eslesme "Diger"e duser, kaybolmaz.
public enum Asama { Hazir, Kural, PusulaIsi, Veri }

public record SebepGrubu(
    string Anahtar, Asama Asama, string Etiket, string Kim, string NeZaman,
    List<(PendingProtocol Protokol, List<PendingItem> Kalemler)> Uyeler)
{
    public int Protokol => Uyeler.Count;
    public int Kalem => Uyeler.Sum(u => u.Kalemler.Count);
    public DateTime? EnEski => Uyeler.Min(u => u.Protokol.Protokol.AcilisTarihi);
}

public record KalemSebebi(string Anahtar, Asama Asama, string Etiket, string Kim, string NeZaman);

public class BekleyenDokum
{
    public int Taranan { get; private init; }
    public int Tamam { get; private init; }
    public int Bekliyor { get; private init; }        // ana kumede kalemi olan protokol
    public int YalnizTakili { get; private init; }    // yalnizca takili kalemi olan protokol
    public int KapsamDisi { get; private init; }      // recete, iptal, taban oncesi
    public bool Kirpik { get; private init; }         // sonuc kirpilmis taramadan geldi

    public required List<SebepGrubu> Gruplar { get; init; }

    public IEnumerable<SebepGrubu> AsamaGruplari(Asama a) =>
        Gruplar.Where(g => g.Asama == a).OrderByDescending(g => g.Protokol).ThenByDescending(g => g.Kalem);

    // Asama kartindaki sayi: o asamada en az bir sebebi olan FARKLI protokol.
    public int AsamaProtokol(Asama a) => AsamaGruplari(a)
        .SelectMany(g => g.Uyeler).Select(u => u.Protokol.Protokol.ProtokolId).Distinct().Count();

    public int AsamaKalem(Asama a) => AsamaGruplari(a).Sum(g => g.Kalem);

    public SebepGrubu? Grup(string anahtar) => Gruplar.FirstOrDefault(g => g.Anahtar == anahtar);

    public static BekleyenDokum Hesapla(PendingWorkResult s)
    {
        var gruplar = new Dictionary<string, SebepGrubu>();
        void Ekle(KalemSebebi k, PendingProtocol p, List<PendingItem> kalemler)
        {
            if (!gruplar.TryGetValue(k.Anahtar, out var g))
                gruplar[k.Anahtar] = g = new SebepGrubu(k.Anahtar, k.Asama, k.Etiket, k.Kim, k.NeZaman, []);
            g.Uyeler.Add((p, kalemler));
        }

        foreach (var p in s.Protokoller)
            Ekle(KuralSebebi(p), p, p.Items);

        foreach (var p in s.TakilanProtokoller)
            foreach (var grup in p.Items.GroupBy(i => TakiliSebebi(p, i)))
                Ekle(grup.Key, p, grup.ToList());

        var bekleyenIdler = s.Protokoller.Select(p => p.Protokol.ProtokolId).ToHashSet();
        var yalnizTakili = s.TakilanProtokoller.Count(p => !bekleyenIdler.Contains(p.Protokol.ProtokolId));
        var k = s.Kapsam;
        return new BekleyenDokum
        {
            Taranan = k.TarananProtokol,
            Tamam = k.TamamlananProtokol,
            Bekliyor = bekleyenIdler.Count,
            YalnizTakili = yalnizTakili,
            KapsamDisi = Math.Max(0, k.TarananProtokol - k.TamamlananProtokol - bekleyenIdler.Count - yalnizTakili),
            Kirpik = s.Protokoller.Count < s.ToplamProtokolSayisi
                     || s.TakilanProtokoller.Count < s.ToplamTakilanProtokolSayisi,
            Gruplar = gruplar.Values.ToList(),
        };
    }

    // ---- 1-2: ana kumedeki protokol neden henuz gitmedi ----
    // Metinler PendingWorkService.IsEligible/UygunlukAni ve Grupla'daki soguma/yatan
    // dallarindan geliyor.
    public static KalemSebebi KuralSebebi(PendingProtocol p)
    {
        var r = p.NotEligibleReason ?? "";
        if (p.Eligible)
            return new("hazir", Asama.Hazir, "Gönderime hazır — sırasını bekliyor", "Sistem", "sıradaki turlarda");
        if (r.StartsWith("Bugün kapandı"))
            return new("bugun", Asama.Kural, "Bugün kapandı — aynı gün gönderilmiyor", "Sistem", "yarın");
        if (r.StartsWith("Kapanalı"))
            return new("bekleme", Asama.Kural, "Kapanış sonrası bekleme süresi dolmadı", "Sistem", "süre dolunca");
        if (r.StartsWith("Protokol açık"))
            return new("acik", Asama.Kural, "Ayaktan protokol hâlâ açık", "Sistem", "kapanınca ya da açık bekleme süresi dolunca");
        if (r.StartsWith("Hasta hâlâ yatıyor"))
            return new("yatan", Asama.Kural, "Yatan hasta taburcu olmadı", "Sistem", "taburcu edilince");
        if (r == PendingWorkService.YatanOtomatikDisiSebep)
            return new("yatan-elle", Asama.Kural, "Yatan — otomatik gönderim kapalı", "Kullanıcı", "Yatan Hastalar ekranından elle");
        if (r.StartsWith("Az önce denendi"))
            return new("soguma", Asama.Kural, "Az önce denendi, hata sonrası bekleme", "Sistem", "bekleme süresi dolunca");
        return new("kural-diger", Asama.Kural, string.IsNullOrWhiteSpace(r) ? "Diğer" : r, "Sistem", "—");
    }

    // ---- 3-4: takili kalem neden takili ----
    // Siralama PendingWorkService.Siniflandir ile AYNI: once zincir (muayene atlandiysa
    // protokolun her kalemi onun yuzunden takili), sonra gonderim disi sebep, sonra son
    // denemenin mesaji.
    public static KalemSebebi TakiliSebebi(PendingProtocol p, PendingItem i)
    {
        var muayene = p.Items.FirstOrDefault(x => x.ResourceType == "Encounter");
        if (muayene?.SonDeneme is { Status: SyncStatus.Skipped, Message: { } mm })
            return MesajSebebi(mm);
        if (!string.IsNullOrEmpty(i.GonderilemezSebep))
            return GonderilemezSebebi(i.GonderilemezSebep);
        if (i.SonDeneme?.Message is { } m)
            return MesajSebebi(m);
        return new("bilinmiyor", Asama.Veri, "Sebep kayıtlı değil", "Bilgi işlem", "—");
    }

    private static KalemSebebi GonderilemezSebebi(string s)
    {
        if (s.Contains("Radyoloji", StringComparison.OrdinalIgnoreCase))
            return new("rad-onay", Asama.PusulaIsi, "Radyoloji raporu onaylanmamış", "Radyoloji", "onaylanınca");
        if (s.Contains("Laboratuvar", StringComparison.OrdinalIgnoreCase))
            return new("lab-onay", Asama.PusulaIsi, "Laboratuvar sonucu onaylanmamış", "Laboratuvar", "onaylanınca");
        if (s.Contains("İcbari", StringComparison.OrdinalIgnoreCase))
            return Icbari;
        if (s.Contains("Hizmet tanımı", StringComparison.OrdinalIgnoreCase))
            return new("hizmet", Asama.Veri, "Hizmet tanımı eksik", "Faturalama", "tanımlanınca");
        return new("gonderilemez", Asama.Veri, s, "Bilgi işlem", "—");
    }

    private static readonly KalemSebebi Icbari =
        new("icbari", Asama.Veri, "İcbari fiyat listesi eşleşmesi yok", "Faturalama", "eşleşme tanımlanınca");

    private static KalemSebebi MesajSebebi(string m)
    {
        var kat = SyncLogEntry.ErrorCategory(m).Label;

        // Kimlik: hasta mi doktor mu? Hastanin FIN'i muayeneyi, dolayisiyla protokolun
        // TAMAMINI kilitliyor -- ikisi farkli kisilere gidiyor.
        var doktor = m.Contains("Doktor", StringComparison.OrdinalIgnoreCase)
                     || m.Contains("Practitioner", StringComparison.OrdinalIgnoreCase);
        if (kat == "Kimlik sorunu" || (kat == "Zincirdeki önceki kayıt gitmedi" && doktor))
            return doktor
                ? new("doktor-fin", Asama.Veri, "Doktorun FIN'i boş / hatalı (epikriz yazarı dahil)", "Personel kartı", "düzeltilince")
                : new("hasta-fin", Asama.Veri, "Hasta FIN'i boş / hatalı — protokolün tamamını kilitliyor", "Hasta kabul", "düzeltilince");

        if (m.Contains("İcbari", StringComparison.OrdinalIgnoreCase) || m.Contains("Icbari", StringComparison.OrdinalIgnoreCase))
            return Icbari;
        if (m.Contains("LOINC", StringComparison.OrdinalIgnoreCase))
            return new("loinc", Asama.Veri, "Tetkikin LOINC kodu yok", "Laboratuvar · LOINC Eşleştirme", "kod verilince");
        if (m.Contains("çok kısa", StringComparison.OrdinalIgnoreCase))
            return new("rapor-kisa", Asama.PusulaIsi, "Rapor metni çok kısa", "Patoloji", "rapor yazılınca");

        return kat switch
        {
            "Gönderilecek metin yok" when m.Contains("Epikriz", StringComparison.OrdinalIgnoreCase)
                => new("epikriz", Asama.PusulaIsi, "Epikriz yazılmamış", "Doktor", "yazılınca"),
            "Gönderilecek metin yok"
                => new("rapor-bos", Asama.PusulaIsi, "Rapor metni boş", "Radyoloji / Patoloji", "yazılınca"),
            "Tetkik sonucu girilmemiş"
                => new("sonuc", Asama.PusulaIsi, "Tetkik sonuç değeri girilmemiş", "Laboratuvar", "sonuç girilince"),
            "Zincirdeki önceki kayıt gitmedi"
                => new("zincir", Asama.PusulaIsi, "Bağlı kayıt henüz gitmedi", "Kendiliğinden", "bağlı kayıt gidince"),
            "ICD tanı eksik/geçersiz"
                => new("icd", Asama.Veri, "Tanı (ICD) kodu TRƏS'te geçersiz", "Doktor / Bilgi işlem", "düzeltilince"),
            "İşlem tarihi müayinə dışında"
                => new("islem-tarihi", Asama.Veri, "İşlem tarihi muayene aralığı dışında", "Faturalama", "düzeltilince"),
            "Doktor dışı personel"
                => new("personel", Asama.Veri, "Doktor dışı personel — bilinçli gönderilmiyor", "—", "gönderilmez"),
            _ => new("diger:" + kat, Asama.Veri, kat, "Bilgi işlem", "—"),
        };
    }
}
