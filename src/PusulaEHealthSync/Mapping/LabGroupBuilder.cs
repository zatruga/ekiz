using PusulaEHealthSync.Db;

namespace PusulaEHealthSync.Mapping;

// Laboratuvar satirlarini PANEL bazinda gruplar.
//
// BAKANLIK ISTEGI (2026-09-16, "öncelikli düzeltme konusu"): alt parametreli tetkikler
// ana tetkik altinda gruplanmali -- ana tetkik Observation, alt parametreler ayni
// kaynagin component[] dizisi icinde. Eskiden her alt parametre AYRI bir Observation
// olarak gidiyordu.
//
// Bu gruplama mantigi onceden SADECE Protokol Detay sayfasinin bir private metodunda
// (gorunum icin) duruyordu ve o dosyadaki not "Gönderim tarafında birleştirme YOK"
// diyordu. Artik gonderim de ayni gruplamayi kullandigi icin mantik CEKIRDEGE tasindi --
// iki kopya olsaydi ekranda gorunen grup ile gonderilen kaynak birbirinden ayrisirdi.
public static class LabGroupBuilder
{
    // Bir gonderim birimi: ya tek sonuclu bagimsiz bir test, ya da alt parametreli bir panel.
    //
    // AnahtarSatir: Observation'in kimligini (local-system-unique-id ve SyncLog.PusulaId)
    // ve kodunu belirleyen satir. Panelde once panelin KENDI satiri, o yoksa uyelerin en
    // kucuk Id'lisi -- her iki durumda da AYNI girdi icin AYNI sonucu verir, yani tekrar
    // gonderimde yeni kaynak acilmaz, mevcut kaynak guncellenir.
    public sealed record LabGrup(
        string Ad,
        string PanelAdi,
        bool Panelli,
        LabResultRecord AnahtarSatir,
        IReadOnlyList<LabResultRecord> Bilesenler,
        IReadOnlyList<LabResultRecord> TumSatirlar)
    {
        public int AnahtarId => AnahtarSatir.LabaratuarSonucId;
    }

    public static List<LabGrup> Build(IReadOnlyList<LabResultRecord> labs)
    {
        // Bir satirin PanelAdi'si varsa dogrudan panel adidir (alt parametre). PanelAdi'si
        // olmayan bir satir, eger TetkikAdi'si BASKA satirlarin PanelAdi'siyla eslesirse
        // panelin KENDI satiridir (orn. "Hemogram").
        var panelAdlari = labs
            .Where(l => !string.IsNullOrWhiteSpace(l.PanelAdi))
            .Select(l => l.PanelAdi!)
            .ToHashSet();

        string? PanelAdiBul(LabResultRecord lab)
        {
            if (!string.IsNullOrWhiteSpace(lab.PanelAdi)) return lab.PanelAdi;
            if (!string.IsNullOrWhiteSpace(lab.TetkikAdi) && panelAdlari.Contains(lab.TetkikAdi)) return lab.TetkikAdi;
            return null;
        }

        // Ayni panel bir yatista birden cok kez istenebiliyor (orn. gunluk "İdrar Tetkiki").
        // Sadece panel adina gore gruplarsak farkli gunlerin sonuclari TEK Observation'da
        // birleserek birbirini ezerdi -- grup anahtarina onay tarihinin GUNU de katiliyor.
        static string GunKovasi(LabResultRecord lab) =>
            (lab.TetkikSonucOnayTarihi ?? lab.TetkikSonucTarihi)?.ToString("yyyy-MM-dd") ?? "-";

        string Anahtar(LabResultRecord lab)
        {
            var panel = PanelAdiBul(lab);
            return panel is null
                ? $"__tek_{lab.LabaratuarSonucId}"
                : $"{panel}||{GunKovasi(lab)}";
        }

        var hamGruplar = labs
            .GroupBy(Anahtar)
            .Select(g =>
            {
                var satirlar = g.ToList();
                var tekMi = g.Key.StartsWith("__tek_", StringComparison.Ordinal);
                var panelAdi = tekMi ? (satirlar[0].TetkikAdi ?? "-") : PanelAdiBul(satirlar[0])!;

                // Panelin KENDI satiri: adi panel adiyla ayni olan satir. Genelde sonuc
                // degeri tasimaz (sadece siparis/toplayici kayittir) -- eski yapida tam bu
                // yuzden "kendi sonuç değeri yok" diye Skipped oluyordu. Yeni yapida sorun
                // degil: artik component tasidigi icin az-lab-value-or-component kuralini
                // saglar, yani bu satirlar da kurtuluyor.
                var panelSatiri = satirlar.FirstOrDefault(x => x.TetkikAdi == panelAdi);

                // component olacak satirlar: panelin kendi satiri disindakiler.
                var bilesenler = satirlar.Where(x => !ReferenceEquals(x, panelSatiri)).ToList();

                // Panel satiri yoksa (orn. "İdrar Mikroskopisi") en kucuk Id temsilci olur;
                // kodlar zaten PanelLoincKodu/PanelIcbariKodu'ndan geliyor.
                var anahtarSatir = panelSatiri ?? satirlar.MinBy(x => x.LabaratuarSonucId)!;

                return (PanelAdi: panelAdi, Gun: GunKovasi(satirlar[0]), TekMi: tekMi,
                        AnahtarSatir: anahtarSatir,
                        Bilesenler: (IReadOnlyList<LabResultRecord>)bilesenler
                            .OrderBy(x => x.TetkikAdi).ToList(),
                        TumSatirlar: (IReadOnlyList<LabResultRecord>)satirlar);
            })
            .ToList();

        // Ayni panel birden fazla gunde tekrarlanmissa basliga tarih eklenir; tek seferlik
        // testlerde gereksiz kalabalik olmasin.
        var tekrarSayisi = hamGruplar.CountBy(g => g.PanelAdi).ToDictionary(x => x.Key, x => x.Value);

        return hamGruplar
            .Select(g =>
            {
                var tarihGoster = tekrarSayisi[g.PanelAdi] > 1 && g.Gun != "-";
                var ad = tarihGoster && DateTime.TryParse(g.Gun, out var t)
                    ? $"{g.PanelAdi} ({t:dd.MM.yyyy})"
                    : g.PanelAdi;

                // "Panelli" = gercekten alt parametresi olan grup. Tek satirlik bir panel
                // (bilesen yok) bagimsiz test gibi davranir -- degeri Observation.value[x]'te
                // gider, bakanligin "tek sonuçlu bağımsız tetkikler" tarifi budur.
                var panelli = !g.TekMi && g.Bilesenler.Count > 0;

                return new LabGrup(ad, g.PanelAdi, panelli, g.AnahtarSatir, g.Bilesenler, g.TumSatirlar);
            })
            .OrderBy(g => g.Ad)
            .ToList();
    }
}
