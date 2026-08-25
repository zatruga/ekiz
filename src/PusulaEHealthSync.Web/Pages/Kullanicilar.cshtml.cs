using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Kullanici yonetimi + yetkilendirme -- sadece Admin rolu erisebilir (bkz. Program.cs
// "AdminOnly" policy). Tek sabit admin hesabinin (DashboardAuthOptions) yerini aldi.
public class KullanicilarModel(UserAccountStore userAccountStore, IPasswordHasher<object> hasher) : PageModel
{
    public List<UserAccount> Users { get; set; } = [];
    public string? Message { get; set; }
    public string? Error { get; set; }

    [BindProperty]
    public string NewUsername { get; set; } = "";

    [BindProperty]
    public string NewPassword { get; set; } = "";

    [BindProperty]
    public string NewRole { get; set; } = UserAccountStore.RoleOperator;

    public string CurrentUsername => User.FindFirstValue(ClaimTypes.Name) ?? "";

    public async Task OnGetAsync(CancellationToken ct)
    {
        Users = await userAccountStore.GetAllAsync(ct);
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewUsername) || string.IsNullOrWhiteSpace(NewPassword))
        {
            Error = "Kullanıcı adı ve şifre zorunludur.";
        }
        else if (NewPassword.Length < 6)
        {
            Error = "Şifre en az 6 karakter olmalı.";
        }
        else
        {
            var hash = hasher.HashPassword(new object(), NewPassword);
            var created = await userAccountStore.CreateAsync(NewUsername.Trim(), hash, NewRole, ct);
            Message = created ? $"'{NewUsername}' kullanıcısı eklendi." : null;
            Error = created ? null : $"'{NewUsername}' adında bir kullanıcı zaten var.";
        }

        Users = await userAccountStore.GetAllAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSetRoleAsync(int id, string role, CancellationToken ct)
    {
        var target = (await userAccountStore.GetAllAsync(ct)).FirstOrDefault(u => u.Id == id);
        if (target is not null && target.Username == CurrentUsername && role != UserAccountStore.RoleAdmin)
        {
            Error = "Kendi Admin yetkinizi kendiniz kaldıramazsınız.";
        }
        else
        {
            await userAccountStore.SetRoleAsync(id, role, ct);
            Message = "Rol güncellendi.";
        }

        Users = await userAccountStore.GetAllAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleActiveAsync(int id, CancellationToken ct)
    {
        var target = (await userAccountStore.GetAllAsync(ct)).FirstOrDefault(u => u.Id == id);
        if (target is null) return RedirectToPage();

        if (target.Username == CurrentUsername)
        {
            Error = "Kendi hesabınızı pasif hale getiremezsiniz.";
        }
        else
        {
            await userAccountStore.SetActiveAsync(id, !target.Active, ct);
            Message = target.Active ? "Kullanıcı pasif hale getirildi." : "Kullanıcı aktif hale getirildi.";
        }

        Users = await userAccountStore.GetAllAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(int id, string resetPassword, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(resetPassword) || resetPassword.Length < 6)
        {
            Error = "Yeni şifre en az 6 karakter olmalı.";
        }
        else
        {
            var hash = hasher.HashPassword(new object(), resetPassword);
            await userAccountStore.SetPasswordAsync(id, hash, ct);
            Message = "Şifre sıfırlandı.";
        }

        Users = await userAccountStore.GetAllAsync(ct);
        return Page();
    }
}
