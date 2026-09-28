using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// "Bekleyen Isler" -- Pusula'da hazir olup e-Health'e gitmemis her sey.
//
// SALT-OKUNUR: bu sayfa HICBIR SEY GONDERMEZ. Amaci, otomatik gonderimi acmadan once
// prod'da neyin biriktigini gorunur kilmak. Gonderim isteniyorsa protokol satirindan
// Protokol Detay'a gidilip oradaki "Tumunu Gonder" kullanilir, ya da Ayarlar'dan otomatik
// gonderim acilir (bkz. AutoSyncWorker).
//
// ONBELLEKTEN OKUR: tam tarama canli veride ~15 sn suruyor, her acilista calistirilamaz.
// Sonuc PendingWorkService'te tutulur; saatlik dongu her turunda tazeler, kullanici da
// "Yenile" ile zorlayabilir.
[Authorize]
public class BekleyenIslerModel(PendingWorkService pendingWork, SettingsStore settings) : PageModel
{
    public PendingWorkResult? Sonuc { get; private set; }
    public DateTime? SonHesaplamaUtc { get; private set; }
    public bool OtomatikGonderimAcik { get; private set; }

    // 0 = "Ayarlar'daki tarama araligini kullan" (KULLANICI ISTEGI 2026-09-28: pencere
    // artik kodda sabit degil, Ayarlar > Tarama Araligi'ndan yonetiliyor). Adres cubuguna
    // ?Gun=14 yazilirsa o tur icin ayar gecici olarak ezilebilir.
    [BindProperty(SupportsGet = true)]
    public int Gun { get; set; }

    // Ekranda gosterilecek gercek baslangic tarihi -- ayar "belirli tarih" modundaysa
    // "son N gun" demek yaniltici olurdu.
    public DateTime TaramaBaslangici { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        OtomatikGonderimAcik = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
        TaramaBaslangici = await BaslangicAsync(ct);
        Sonuc = pendingWork.SonSonuc;
        SonHesaplamaUtc = pendingWork.SonHesaplamaUtc;
    }

    public async Task<IActionResult> OnPostYenileAsync(CancellationToken ct)
    {
        await pendingWork.RefreshAsync(Gun > 0 ? Gun : null, 200, ct);
        return RedirectToPage(new { Gun });
    }

    private async Task<DateTime> BaslangicAsync(CancellationToken ct) =>
        Gun > 0
            ? DateTime.Now.Date.AddDays(-Gun)
            : await PendingWorkService.TaramaBaslangiciAsync(settings, ct);
}
