using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// GIRISTEN SONRA HTTP 400 (2026-10-23, kullanici bildirdi: "her girisimden sonra bu hatayi
// aliyorum, geri butonuna basiyorum ve sistem aciliyor").
//
// YERELDE URETILDI: ayni giris formu IKI KEZ gonderilince ilki 302 ile oturumu aciyor, ikincisi
// 400 donuyordu. Sebep antiforgery: belirtec ANONIM kullaniciya uretilmisti, ikinci gonderim
// ise artik giris yapmis kullaniciyla geliyor ve ASP.NET bunu "baska bir kullanici icin
// uretilmis belirtec" diye reddediyor. Kullanici aslinda iceride oldugu icin "geri" ile
// sistem aciliyordu. Tarayicinin formu neden iki kez gonderdigi sunucu kaydi olmadan
// bilinemedi (Enter + tik, tarayicinin yeniden denemesi) -- duzeltme sebebe baglanmadi:
//   - Zaten giris yapilmissa ikinci gonderim DOGRUDAN yonlendirilir (sifre yeniden denenmez).
//   - Belirtec baska sebeple gecersizse ciplak 400 yerine yeni bir formla mesaj gosterilir.
//     Belirtec DENETIMI KALKMADI, yalnizca elle yapiliyor ki hata okunabilir olsun.
[IgnoreAntiforgeryToken]
public class LoginModel(UserAccountStore userAccountStore, IPasswordHasher<object> hasher,
    Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? Error { get; set; }

    public IActionResult OnGet(string? returnUrl = null) =>
        User.Identity?.IsAuthenticated == true ? Yonlendir(returnUrl) : Page();

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return Yonlendir(returnUrl);

        if (!await antiforgery.IsRequestValidAsync(HttpContext))
        {
            Error = "Giriş formunun süresi dolmuş. Lütfen bilgilerinizi yeniden girip tekrar deneyin.";
            return Page();
        }

        var user = await userAccountStore.GetByUsernameAsync(Username);
        var passwordOk = user is not null
            && hasher.VerifyHashedPassword(new object(), user.PasswordHash, Password) != PasswordVerificationResult.Failed;

        if (user is null || !user.Active || !passwordOk)
        {
            Error = user is { Active: false }
                ? "Bu kullanıcı hesabı pasif durumda."
                : "Kullanıcı adı veya şifre hatalı.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return Yonlendir(returnUrl);
    }

    // Yalnizca YEREL adrese: ReturnUrl disaridan gelebilir (acik yonlendirme olmasin).
    private IActionResult Yonlendir(string? returnUrl) =>
        LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Index");
}
