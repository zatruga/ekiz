using System.ComponentModel.DataAnnotations;
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

    // TARIH ARALIGI (KULLANICI ISTEGI 2026-09-29): "bekleyen islere 2 tarih secimi
    // koyalim, baslangic tarihi ve bitme tarihi, bu iki tarih arasini hesaplasin."
    //
    // Bos birakilirsa Ayarlar > Tarama Araligi'ndaki pencere kullanilir; boylece sayfa
    // ilk acildiginda ayarla ayni sonucu gosterir. Bitis GUN SONUNA kadar dahildir
    // (sorguya bir sonraki gunun 00:00'i gonderiliyor) -- kullanici 29.09 sectiginde
    // 29.09'da sonuclanmis kayitlar da listeye girmeli.
    [BindProperty(SupportsGet = true)]
    [DataType(DataType.Date)]
    public DateOnly? Baslangic { get; set; }

    [BindProperty(SupportsGet = true)]
    [DataType(DataType.Date)]
    public DateOnly? Bitis { get; set; }

    // Ekranda gosterilen fiili aralik.
    public DateOnly EtkinBaslangic { get; private set; }
    public DateOnly EtkinBitis { get; private set; }
    public string? UyariMesaji { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        OtomatikGonderimAcik = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
        await AralikHesaplaAsync(ct);
        Sonuc = pendingWork.SonSonuc;
        SonHesaplamaUtc = pendingWork.SonHesaplamaUtc;
    }

    public async Task<IActionResult> OnPostYenileAsync(CancellationToken ct)
    {
        await AralikHesaplaAsync(ct);

        // Bitis gun sonuna kadar dahil -> ertesi gunun 00:00'i (haric) gonderiliyor.
        await pendingWork.RefreshAsync(
            scanDays: null,
            maxProtocols: 200,
            ct: ct,
            fromLocalOverride: EtkinBaslangic.ToDateTime(TimeOnly.MinValue),
            toLocalExclusive: EtkinBitis.AddDays(1).ToDateTime(TimeOnly.MinValue));

        return RedirectToPage(new { Baslangic = EtkinBaslangic.ToString("yyyy-MM-dd"), Bitis = EtkinBitis.ToString("yyyy-MM-dd") });
    }

    private async Task AralikHesaplaAsync(CancellationToken ct)
    {
        var bugun = DateOnly.FromDateTime(DateTime.Now);
        EtkinBaslangic = Baslangic
            ?? DateOnly.FromDateTime(await PendingWorkService.TaramaBaslangiciAsync(settings, ct));
        EtkinBitis = Bitis ?? bugun;

        // Ters aralik girilirse sessizce bos liste gostermek yerine duzelt ve soyle --
        // kullanici "hic bekleyen is yok" sanmasin.
        if (EtkinBitis < EtkinBaslangic)
        {
            (EtkinBaslangic, EtkinBitis) = (EtkinBitis, EtkinBaslangic);
            UyariMesaji = "Bitiş tarihi başlangıçtan önceydi, tarihler yer değiştirildi.";
        }
    }
}
