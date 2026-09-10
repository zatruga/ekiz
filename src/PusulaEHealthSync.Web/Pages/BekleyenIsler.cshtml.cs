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

    [BindProperty(SupportsGet = true)]
    public int Gun { get; set; } = PendingWorkService.DefaultScanDays;

    public async Task OnGetAsync(CancellationToken ct)
    {
        OtomatikGonderimAcik = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
        Sonuc = pendingWork.SonSonuc;
        SonHesaplamaUtc = pendingWork.SonHesaplamaUtc;
    }

    public async Task<IActionResult> OnPostYenileAsync(CancellationToken ct)
    {
        await pendingWork.RefreshAsync(Gun, 200, ct);
        return RedirectToPage(new { Gun });
    }
}
