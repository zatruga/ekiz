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

        return [.. liste];
    }

    public async Task OnGetAsync(bool saved = false, CancellationToken ct = default)
    {
        Saved = saved;
        var liste = await FiltreleAsync(ct);
        GosterilenSayi = liste.Count;
        // 20.613 satiri birden cizmek sayfayi agirlastiriyor -- hacme gore ilk 400,
        // daraltmak icin filtreler var. Excel'e aktarim TUM filtre sonucunu alir.
        Satirlar = [.. liste.Take(400)];
    }

    public async Task<IActionResult> OnPostKaydetAsync(int hizmetId, string? azKod, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, azKod, gonderilmez: false, ct);
        return RedirectToPage("/HizmetEslestirme", new { saved = true, Durum, Tip, Ara });
    }

    public async Task<IActionResult> OnPostGonderilmezAsync(int hizmetId, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, null, gonderilmez: true, ct);
        return RedirectToPage("/HizmetEslestirme", new { saved = true, Durum, Tip, Ara });
    }

    public async Task<IActionResult> OnPostSifirlaAsync(int hizmetId, CancellationToken ct)
    {
        await store.SifirlaAsync(hizmetId, ct);
        return RedirectToPage("/HizmetEslestirme", new { saved = true, Durum, Tip, Ara });
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
            new("Pusula Hizmet Kodu", 20),
            new("Hizmet Adı", 52),
            new("Hizmet Tipi", 22),
            new("İstem (365 gün)", 16, Sayi: true),
            new("Mevcut Eşleşme (İcbari)", 22),
            new("Önerilen Bakanlık Kodu", 22),
            new("Öneri Kaynağı", 15),
            new("Bakanlık Adı (AZ)", 62),
            new("Bakanlık Adı (TR)", 62),
            new("▶ PUSULA'YA GİRİLEN KOD", 26),
        };

        var satirlar = liste.Select(s =>
        {
            var kod = s.EtkinKod;
            return new string?[]
            {
                s.HizmetKodu,
                s.HizmetAdi,
                s.HizmetTipi,
                s.Istem.ToString(System.Globalization.CultureInfo.InvariantCulture),
                s.IcbariKodu,
                s.AzKod ?? s.Oneri,
                s.Gonderilmez ? "gönderilmez" : s.Rozet switch
                {
                    HizmetMappingStore.KaynakPusula => "Pusula",
                    HizmetMappingStore.KaynakMedigate => "Medigate",
                    HizmetMappingStore.KaynakKullanici => "elle",
                    _ => "",
                },
                AzAdi(kod),
                AzAdiTr(kod),
                "",
            };
        });

        var bayt = XlsxWriter.Olustur("Hizmet Eşleştirme", sutunlar, satirlar);
        var ad = $"hizmet-eslestirme-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ad);
    }
}
