using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

public class LoginModel(UserAccountStore userAccountStore, IPasswordHasher<object> hasher) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
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

        return LocalRedirect(!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/Index");
    }
}
