using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Reporting;

namespace PusulaEHealthSync.Web.Pages;

// GUN SONU RAPORU ON IZLEMESI (2026-10-05).
//
// NEDEN AYRI BIR SAYFA: rapor e-postayla gidiyor, yani normalde ancak saat 07:00'de ve
// yalnizca alici listesindekiler gorebiliyor. Bu iki sorun demekti:
//   1. Raporun BICIMINI degistirince sonucu gormek icin ertesi sabahi beklemek gerekiyordu.
//   2. Alici listesinde olmayan biri (orn. gun icinde merak eden hekim) raporu hic goremez.
//
// Sayfa mail GONDERMEZ -- yalnizca ayni HTML'i tarayiciya basar. E-posta govdesi ile
// ekrandaki ciktinin AYNI koddan (GunSonuMailHtml) gelmesi bilincli: iki ayri sablon
// kacinilmaz olarak birbirinden ayrisir ve o an "mailde farkli gorunuyor" olarak patlar.
//
// DIKKAT -- E-POSTA HTML'I TARAYICIDA DAHA IYI GORUNUR: Outlook'un Word motoru bu HTML'in
// bir kismini (border-radius, bazi padding'ler) yoksayar. Ekrandaki goruntunun duzgun
// olmasi mailin de duzgun oldugunu KANITLAMAZ; gercek kontrol "Test maili gönder".
[Authorize]
public class GunSonuRaporuModel(GunSonuRaporService rapor) : PageModel
{
    // Hangi gun? Varsayilan dun -- raporun normal kapsami (bkz. AutoSyncWorker).
    // Tarih verilebiliyor ki gecmis bir gunun bilancosu da bakilabilsin.
    [BindProperty(SupportsGet = true)]
    public DateOnly? Gun { get; set; }

    public string Html { get; private set; } = "";
    public DateOnly GosterilenGun { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        GosterilenGun = Gun ?? DateOnly.FromDateTime(DateTime.Now.AddDays(-1));
        var r = await rapor.OlusturAsync(GosterilenGun, ct);
        Html = GunSonuMailHtml.Govde(r);
    }
}
