using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web;

// ARSIV ROLU YALNIZCA YATAN HASTALAR EKRANINI GORUR (2026-10-26, kullanici karari).
//
// NEDEN FILTRE, POLITIKA DEGIL: sayfalar bugun "giris yapmis herkes" kuraliyla acik
// (AuthorizeFolder("/")). Her sayfaya "Arsiv haric" politikasi eklemek, sonradan eklenen
// her yeni sayfada unutulabilecek bir adim demekti -- unutulan sayfa arsivciye sessizce
// acilirdi. Tersine bir izin listesi daha guvenli: arsivci icin acik sayfalar burada
// sayili, digerleri kendiliginden kapali. Diger rollerin yetkileri hic degismiyor.
//
// Statik dosyalar (css/js) Razor sayfasi olmadigi icin bu filtreden gecmez.
public class ArsivSayfaKisiti : IAsyncPageFilter
{
    private static readonly HashSet<string> Acik = new(StringComparer.OrdinalIgnoreCase)
    {
        "/YatanHastalar", "/Login", "/Logout", "/Error",
    };

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (context.HttpContext.User.IsInRole(UserAccountStore.RoleArsiv)
            && !Acik.Contains(context.ActionDescriptor.ViewEnginePath))
        {
            context.Result = new RedirectToPageResult("/YatanHastalar");
            return;
        }
        await next();
    }
}
