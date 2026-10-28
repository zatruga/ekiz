using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Export;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Pusula hizmeti -> AZ az-procedure-codes eslestirmesi.
//
// KULLANICI KARARI (2026-09-24): ASIL ESLESTIRME PUSULA'DA YAPILACAK. Bu ekran
// calisma listesi ve oneri aracidir:
//   - Pusula'daki TUM hizmetler listelenir
//   - Eslesmesi Pusula'dan (Icbari Sigorta Fiyat Listesi) geliyorsa "Pusula" rozeti
//   - Bizim onerimiz varsa "Medigate" rozeti -- henuz Pusula'ya islenmemis TEKLIF
//   - Eslesmeyenler CHECKBOX ile secilip Excel'e aktarilir, eslestirme Pusula'da yapilir
public class HizmetEslestirmeModel(HizmetMappingStore store) : PageModel
{
    public List<HizmetMappingStore.Satir> Satirlar { get; set; } = [];
    public List<string> Tipler { get; set; } = [];

    [BindProperty(SupportsGet = true)] public string Durum { get; set; } = "Tumu";
    [BindProperty(SupportsGet = true)] public string? Tip { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ara { get; set; }

    // LISTE SUZGECI VE SIRALAMA (KULLANICI ISTEGI 2026-10-28): "alt listede eslesen ve
    // eslesmeyenleri suzebilecegimiz sayi alani olsun, sutun basliklari siralansin".
    // Esles: ""=tumu, "evet", "hayir", "gonderilmez" -- ustteki Durum/Tip/Ara'nin
    // SONUCU icinde calisir, sayilari da o sonuctan (EslesTumu...).
    // Siralama SUNUCUDA: ekranda en fazla 400 satir var, tarayicida siralamak yalnizca
    // onlari siralardi. Excel de ayni sirayla iner.
    [BindProperty(SupportsGet = true)] public string? Esles { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sira { get; set; }
    [BindProperty(SupportsGet = true)] public string? Yon { get; set; }

    public int EslesTumu { get; set; }
    public int EslesEvet { get; set; }
    public int EslesHayir { get; set; }
    public int EslesGonderilmez { get; set; }
    public const int EkranSiniri = 400;

    // Formlar ve yonlendirmeler arasinda tasinan filtre/sira durumu.
    public object Rota(bool saved = false) => new { saved, Durum, Tip, Ara, Esles, Sira, Yon };

    public static string KaynakAdi(HizmetMappingStore.Satir s) => s.Gonderilmez ? "gönderilmez" : s.Rozet switch
    {
        HizmetMappingStore.KaynakPusula => "Pusula",
        HizmetMappingStore.KaynakMedigate => "Medigate",
        HizmetMappingStore.KaynakKullanici => "elle",
        _ => "",
    };

    public bool Saved { get; set; }
    public int ToplamSayi { get; set; }
    public int PusulaSayi { get; set; }
    public int MedigateSayi { get; set; }
    public int EksikSayi { get; set; }
    public int GonderilmezSayi { get; set; }
    public int ToplamIstem { get; set; }
    public int EslesenIstem { get; set; }
    public int GosterilenSayi { get; set; }
    public int KullanilanSayi { get; set; }
    public int EksikKullanilanSayi { get; set; }

    public static string? AzAdi(string? kod) => AzProcedureCodes.Display(kod);
    public static string? AzAdiTr(string? kod) => AzProcedureCodes.DisplayTr(kod);

    private async Task<List<HizmetMappingStore.Satir>> FiltreleAsync(CancellationToken ct)
    {
        var hepsi = await store.GetAllAsync(ct);

        ToplamSayi = hepsi.Count;
        PusulaSayi = hepsi.Count(s => s.Rozet == HizmetMappingStore.KaynakPusula);
        MedigateSayi = hepsi.Count(s => s.Rozet == HizmetMappingStore.KaynakMedigate);
        GonderilmezSayi = hepsi.Count(s => s.Gonderilmez);
        EksikSayi = hepsi.Count(s => s.PusuladaEksik && !s.Gonderilmez);
        KullanilanSayi = hepsi.Count(s => s.Istem > 0);
        EksikKullanilanSayi = hepsi.Count(s => s.Istem > 0 && s.PusuladaEksik && !s.Gonderilmez);
        ToplamIstem = hepsi.Sum(s => s.Istem);
        EslesenIstem = hepsi.Where(s => s.Eslesti).Sum(s => s.Istem);
        Tipler = [.. hepsi.Select(s => s.HizmetTipi).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().OrderBy(t => t)];

        var liste = Durum switch
        {
            "Pusula" => hepsi.Where(s => s.Rozet == HizmetMappingStore.KaynakPusula),
            "Medigate" => hepsi.Where(s => s.Rozet == HizmetMappingStore.KaynakMedigate),
            "Eksik" => hepsi.Where(s => s.PusuladaEksik && !s.Gonderilmez),
            "Gonderilmez" => hepsi.Where(s => s.Gonderilmez),
            // Katalogda 20.613 hizmet var ama 17.008'i son bir yilda HIC istenmemis.
            // Bu filtre gercekten kullanilan kalemlere odaklanmayi sagliyor.
            "Kullanimda" => hepsi.Where(s => s.Istem > 0),
            "KullanimdaEksik" => hepsi.Where(s => s.Istem > 0 && s.PusuladaEksik && !s.Gonderilmez),
            _ => hepsi.AsEnumerable(),
        };

        if (!string.IsNullOrWhiteSpace(Tip))
            liste = liste.Where(s => s.HizmetTipi == Tip);

        if (!string.IsNullOrWhiteSpace(Ara))
        {
            var q = Ara.Trim();
            liste = liste.Where(s =>
                s.HizmetAdi.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.HizmetKodu.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.IcbariKodu.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.AzKod ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.Oneri ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var ara = liste.ToList();
        EslesTumu = ara.Count;
        EslesEvet = ara.Count(s => s.Eslesti);
        EslesHayir = ara.Count(s => s.Bekliyor);
        EslesGonderilmez = ara.Count(s => s.Gonderilmez);
        IEnumerable<HizmetMappingStore.Satir> sonuc = Esles switch
        {
            "evet" => ara.Where(s => s.Eslesti),
            "hayir" => ara.Where(s => s.Bekliyor),
            "gonderilmez" => ara.Where(s => s.Gonderilmez),
            _ => ara,
        };
        return Sirala(sonuc);
    }

    private List<HizmetMappingStore.Satir> Sirala(IEnumerable<HizmetMappingStore.Satir> liste)
    {
        // Varsayilan: magazanin sirasi (istem cok olandan aza).
        if (string.IsNullOrEmpty(Sira)) return [.. liste];
        var tr = StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true);
        var azalan = Yon == "desc";
        IOrderedEnumerable<HizmetMappingStore.Satir> Metin(Func<HizmetMappingStore.Satir, string?> f) =>
            azalan ? liste.OrderByDescending(s => f(s) ?? "", tr) : liste.OrderBy(s => f(s) ?? "", tr);
        IOrderedEnumerable<HizmetMappingStore.Satir> Sayi(Func<HizmetMappingStore.Satir, int> f) =>
            azalan ? liste.OrderByDescending(f) : liste.OrderBy(f);
        return Sira switch
        {
            "id" => [.. Sayi(s => s.HizmetId)],
            "kod" => [.. Metin(s => s.HizmetKodu)],
            "ad" => [.. Metin(s => s.HizmetAdi)],
            "tip" => [.. Metin(s => s.HizmetTipi)],
            "istem" => [.. Sayi(s => s.Istem)],
            "azkod" => [.. Metin(s => s.EtkinKod)],
            "kaynak" => [.. Metin(KaynakAdi)],
            _ => [.. liste],
        };
    }

    public async Task OnGetAsync(bool saved = false, CancellationToken ct = default)
    {
        Saved = saved;
        var liste = await FiltreleAsync(ct);
        GosterilenSayi = liste.Count;
        // 20.613 satiri birden cizmek sayfayi agirlastiriyor -- hacme gore ilk 400,
        // daraltmak icin filtreler var. Excel'e aktarim TUM filtre sonucunu alir.
        Satirlar = [.. liste.Take(EkranSiniri)];
    }

    public async Task<IActionResult> OnPostKaydetAsync(int hizmetId, string? azKod, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, azKod, gonderilmez: false, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    public async Task<IActionResult> OnPostGonderilmezAsync(int hizmetId, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, null, gonderilmez: true, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    // "Gonderilmez" isaretini kaldirir; varsa elle girilmis kod korunur.
    public async Task<IActionResult> OnPostGonderilsinAsync(int hizmetId, CancellationToken ct)
    {
        var mevcut = (await store.GetAllAsync(ct)).FirstOrDefault(s => s.HizmetId == hizmetId);
        await store.SetAsync(hizmetId, mevcut?.AzKod, gonderilmez: false, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    public async Task<IActionResult> OnPostSifirlaAsync(int hizmetId, CancellationToken ct)
    {
        await store.SifirlaAsync(hizmetId, ct);
        return RedirectToPage("/HizmetEslestirme", Rota(saved: true));
    }

    /// <summary>
    /// Secilen hizmetleri Excel'e aktarir. Dosya Pusula'da eslestirme yapmak icin
    /// kullanilacak: son sutun BOS birakiliyor, oraya Pusula'ya girilen kod yazilir.
    /// Hicbiri secilmemisse mevcut filtrenin TAMAMI aktarilir.
    /// </summary>
    public async Task<IActionResult> OnPostExcelAsync(int[]? secili, CancellationToken ct)
    {
        var liste = await FiltreleAsync(ct);
        if (secili is { Length: > 0 })
        {
            var küme = secili.ToHashSet();
            liste = [.. liste.Where(s => küme.Contains(s.HizmetId))];
        }

        var sutunlar = new List<XlsxWriter.Sutun>
        {
            new("Pusula Hizmet Id", 16, Sayi: true),
            new("Pusula Hizmet Kodu", 20),
            new("Hizmet Adı", 52),
            new("Hizmet Tipi", 22),
            new("İstem (365 gün)", 16, Sayi: true),
            new("Pay %", 10, Sayi: true),
            new("Kümülatif %", 13, Sayi: true),
            new("Mevcut Eşleşme (İcbari)", 22),
            new("Önerilen Bakanlık Kodu", 22),
            new("Öneri Kaynağı", 15),
            new("Bakanlık Adı (AZ)", 62),
            new("Bakanlık Adı (TR)", 62),
            new("▶ PUSULA'YA GİRİLEN KOD", 26),
        };

        // PAY VE KUMULATIF PAY (2026-10-24, kullanici: "kullanilan ama eslesmesi olmayan
        // populer hizmetlerin listesi -- bu oranlara gore eslestirme yaptirmaliyim").
        // Payda: indirilen listenin toplam istemi. "Kullanimda + eslesme yok" filtresiyle
        // indirildiginde kumulatif sutun "ilk N hizmet eslesirse eksigin yuzde kaci
        // kapanir" sorusunu dogrudan cevapliyor. Varsayilan sira istem sirasi; baslikla
    // baska siralanmissa kumulatif o sirayi izler.
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var toplamIstem = Math.Max(1, liste.Sum(s => s.Istem));
        var birikim = 0;
        var satirlar = liste.Select(s =>
        {
            var kod = s.EtkinKod;
            birikim += s.Istem;
            return new string?[]
            {
                s.HizmetId.ToString(inv),
                s.HizmetKodu,
                s.HizmetAdi,
                s.HizmetTipi,
                s.Istem.ToString(inv),
                (s.Istem * 100.0 / toplamIstem).ToString("0.00", inv),
                (birikim * 100.0 / toplamIstem).ToString("0.0", inv),
                s.IcbariKodu,
                s.AzKod ?? s.Oneri,
                KaynakAdi(s),
                AzAdi(kod),
                AzAdiTr(kod),
                "",
            };
        }).ToList();   // birikim sirali sayaca dayaniyor -- tek seferde, sirayla uretilmeli

        var bayt = XlsxWriter.Olustur("Hizmet Eşleştirme", sutunlar, satirlar);
        var ad = $"hizmet-eslestirme-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ad);
    }
}
