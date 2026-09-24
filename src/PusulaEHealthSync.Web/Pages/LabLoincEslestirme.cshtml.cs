using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Lab test kodu -> LOINC eslestirmesi. Pusula SALT OKUNUR oldugu icin bu eslestirme
// LIS.Test.LoincKodu'na yazilamiyor; kendi veritabanimizda tutuluyor ve gonderim
// aninda uygulaniyor (bkz. LabTestLoincStore). Bolum Eslestirme sayfasiyla ayni kalip.
public class LabLoincEslestirmeModel(LabTestLoincStore store) : PageModel
{
    public List<LabTestLoincStore.Satir> Satirlar { get; set; } = [];

    [BindProperty(SupportsGet = true)]
    public string Durum { get; set; } = "Tumu";

    [BindProperty(SupportsGet = true)]
    public string? Ara { get; set; }

    public bool Saved { get; set; }
    public int ToplamSayi { get; set; }
    public int OneriSayi { get; set; }
    public int LabSayi { get; set; }
    public int BelirsizSayi { get; set; }
    public int BosSayi { get; set; }
    public int ToplamIstem { get; set; }
    public int KapaliIstem { get; set; }

    // Standart LOINC adi -- kullanici girdigi kodun ne anlama geldigini ANINDA gorsun
    // diye. Tabloda yoksa null doner; bu, kodun gomulu listede olmadigini gosterir
    // (yanlis olmak zorunda degil, ama dikkat etmeye deger).
    public static string? LoincAdi(string? kod) => Loinc.Display(kod);

    // KULLANICI ISTEGI (2026-09-24): Ingilizce LOINC adi laboratuvara yetmiyor,
    // Turkce karsiligi da gorunsun. Yoksa null doner, ekran Ingilizceye duser.
    public static string? LoincAdiTr(string? kod) => Loinc.DisplayTr(kod);

    public async Task OnGetAsync(bool saved = false, CancellationToken ct = default)
    {
        Saved = saved;
        var hepsi = await store.GetAllAsync(ct);

        ToplamSayi = hepsi.Count;
        LabSayi = hepsi.Count(s => s.Kaynak == LabTestLoincStore.KaynakLaboratuvar);
        OneriSayi = hepsi.Count(s => s.Kaynak == LabTestLoincStore.KaynakOneri && !s.Bekliyor);
        BelirsizSayi = hepsi.Count(s => s.Bekliyor && s.GrupAdi == "belirsiz");
        BosSayi = hepsi.Count(s => s.Bekliyor && s.GrupAdi == "yok");

        // Ilerleme gostergesi: kod atanmis testlerin yillik istem PAYI. Satir sayisi
        // yaniltici olurdu -- 247 kaydin cogu yilda 20 kez isteniyor, biri 27.408 kez.
        ToplamIstem = hepsi.Sum(s => s.Istem);
        KapaliIstem = hepsi.Where(s => !s.Bekliyor).Sum(s => s.Istem);

        var liste = Durum switch
        {
            "Oneri" => hepsi.Where(s => s.Kaynak == LabTestLoincStore.KaynakOneri && !s.Bekliyor),
            "Laboratuvar" => hepsi.Where(s => s.Kaynak == LabTestLoincStore.KaynakLaboratuvar),
            "Belirsiz" => hepsi.Where(s => s.Bekliyor && s.GrupAdi == "belirsiz"),
            "Bos" => hepsi.Where(s => s.Bekliyor && s.GrupAdi == "yok"),
            _ => hepsi.AsEnumerable(),
        };

        if (!string.IsNullOrWhiteSpace(Ara))
        {
            var q = Ara.Trim();
            liste = liste.Where(s =>
                s.PusulaKodu.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.LoincKodu.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (s.TestAdi ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        Satirlar = [.. liste];
    }

    public async Task<IActionResult> OnPostKaydetAsync(
        string pusulaKodu, string? loincKodu, string? testAdi, CancellationToken ct)
    {
        await store.SetAsync(pusulaKodu, loincKodu, testAdi, ct);
        return RedirectToPage("/LabLoincEslestirme", new { saved = true, Durum, Ara });
    }

    public async Task<IActionResult> OnPostSilAsync(string pusulaKodu, CancellationToken ct)
    {
        await store.DeleteAsync(pusulaKodu, ct);
        return RedirectToPage("/LabLoincEslestirme", new { saved = true, Durum, Ara });
    }

    public async Task<IActionResult> OnPostEkleAsync(
        string yeniPusulaKodu, string? yeniLoincKodu, string? yeniTestAdi, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(yeniPusulaKodu))
            await store.SetAsync(yeniPusulaKodu, yeniLoincKodu, yeniTestAdi, ct);
        return RedirectToPage("/LabLoincEslestirme", new { saved = true, Durum, Ara });
    }
}
