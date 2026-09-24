using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Pusula hizmeti -> AZ az-procedure-codes eslestirmesi.
//
// Lab LOINC ekraniyla ayni kalip, ama burada UC secenek var (bkz. HizmetMappingStore):
// kod gir / "gonderilmez" isaretle / oldugu gibi birak. Ucuncu secenek sart, cunku
// eslesmeyen 2.017 hizmetin cogu tibbi islem degil (yatak ucreti, recete islemi,
// CD ucreti) -- bakanligin listesinde karsiligi olmamasi normal.
public class HizmetEslestirmeModel(HizmetMappingStore store) : PageModel
{
    public List<HizmetMappingStore.Satir> Satirlar { get; set; } = [];
    public List<string> Tipler { get; set; } = [];

    [BindProperty(SupportsGet = true)] public string Durum { get; set; } = "Tumu";
    [BindProperty(SupportsGet = true)] public string? Tip { get; set; }
    [BindProperty(SupportsGet = true)] public string? Ara { get; set; }

    public bool Saved { get; set; }
    public int ToplamSayi { get; set; }
    public int EslesenSayi { get; set; }
    public int BekleyenSayi { get; set; }
    public int GonderilmezSayi { get; set; }
    public int ElleSayi { get; set; }
    public int ToplamIstem { get; set; }
    public int KararliIstem { get; set; }

    public static string? AzAdi(string? kod) => AzProcedureCodes.Display(kod);
    public static string? AzAdiTr(string? kod) => AzProcedureCodes.DisplayTr(kod);
    public static bool BakanliktaVar(string? kod) => AzProcedureCodes.Iceriyor(kod);

    public async Task OnGetAsync(bool saved = false, CancellationToken ct = default)
    {
        Saved = saved;
        var hepsi = await store.GetAllAsync(ct);

        ToplamSayi = hepsi.Count;
        EslesenSayi = hepsi.Count(s => s.Eslesti);
        BekleyenSayi = hepsi.Count(s => s.Bekliyor);
        GonderilmezSayi = hepsi.Count(s => s.Gonderilmez);
        ElleSayi = hepsi.Count(s => s.Kaynak == HizmetMappingStore.KaynakKullanici);

        // Ilerleme: KARAR VERILMIS kalemlerin istem payi. "Karar" hem kod atamak hem de
        // "gonderilmez" demek -- ikisi de bir sonuca baglamis sayilir.
        ToplamIstem = hepsi.Sum(s => s.Istem);
        KararliIstem = hepsi.Where(s => !s.Bekliyor).Sum(s => s.Istem);

        Tipler = [.. hepsi.Select(s => s.HizmetTipi).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().OrderBy(t => t)];

        var liste = Durum switch
        {
            "Eslesen" => hepsi.Where(s => s.Eslesti),
            "Bekleyen" => hepsi.Where(s => s.Bekliyor),
            "Gonderilmez" => hepsi.Where(s => s.Gonderilmez),
            "Elle" => hepsi.Where(s => s.Kaynak == HizmetMappingStore.KaynakKullanici),
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
                || (s.AzKod ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        // Ekran 3.604 satiri birden cizmesin -- hacme gore ilk 300, filtreyle daraltilir.
        Satirlar = [.. liste.Take(300)];
    }

    public async Task<IActionResult> OnPostKaydetAsync(
        int hizmetId, string? azKod, string? gonderilmez, CancellationToken ct)
    {
        var isaret = gonderilmez == "on" || gonderilmez == "true";
        await store.SetAsync(hizmetId, isaret ? null : azKod, isaret, ct);
        return RedirectToPage("/HizmetEslestirme", new { saved = true, Durum, Tip, Ara });
    }

    public async Task<IActionResult> OnPostGonderilmezAsync(int hizmetId, CancellationToken ct)
    {
        await store.SetAsync(hizmetId, null, true, ct);
        return RedirectToPage("/HizmetEslestirme", new { saved = true, Durum, Tip, Ara });
    }

    public async Task<IActionResult> OnPostSifirlaAsync(int hizmetId, CancellationToken ct)
    {
        await store.SifirlaAsync(hizmetId, ct);
        return RedirectToPage("/HizmetEslestirme", new { saved = true, Durum, Tip, Ara });
    }
}
