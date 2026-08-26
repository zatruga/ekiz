using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Web.Config;

namespace PusulaEHealthSync.Web.Pages;

// Bu sayfa asil guncelleme islemini YAPMAZ -- sadece Updater servisine (ayri bir Windows
// Service, bkz. PusulaEHealthSync.Updater) bir tetikleyici dosya birakir. Sebep: bu sayfayi
// sunan IIS sureci, kendi calisan dosyalarinin uzerine yazilmasini guvenli sekilde
// tetikleyemez (dosya kilidi + istek yari yolda kesilir). Durum bilgisi de ayni sekilde
// Updater'in yazdigi dosyadan okunuyor (bkz. OnGetDurum, sayfadaki polling).
public class GuncelleModel(IOptions<DeployOptions> optionsAccessor) : PageModel
{
    private readonly DeployOptions options = optionsAccessor.Value;

    private const string UpdateTriggerFile = "update.trigger";
    private const string RollbackTriggerFile = "rollback.trigger";
    private const string StatusFile = "update-status.json";

    public UpdateStatusView? Durum { get; set; }

    public void OnGet()
    {
        Durum = OkuDurum();
    }

    public IActionResult OnPostGuncelleAsync()
    {
        Directory.CreateDirectory(options.ControlPath);
        System.IO.File.WriteAllText(Path.Combine(options.ControlPath, UpdateTriggerFile), User.Identity?.Name ?? "bilinmiyor");
        return RedirectToPage();
    }

    public IActionResult OnPostGeriAlAsync()
    {
        Directory.CreateDirectory(options.ControlPath);
        System.IO.File.WriteAllText(Path.Combine(options.ControlPath, RollbackTriggerFile), User.Identity?.Name ?? "bilinmiyor");
        return RedirectToPage();
    }

    public JsonResult OnGetDurum() => new(OkuDurum());

    private UpdateStatusView? OkuDurum()
    {
        var path = Path.Combine(options.ControlPath, StatusFile);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<UpdateStatusView>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}

// Updater/Models/UpdateStatus.cs ile ayni sekli tasiyan bagimsiz bir kopya -- iki proje
// arasinda paylasilan bir kutuphane yok (bkz. Updater projesinin bilerek bagimsiz
// tutulmasi), sadece dosya araciligiyla haberlesiyorlar.
public class UpdateStatusView
{
    public string State { get; set; } = "Idle";
    public string? Operation { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? BackupFolder { get; set; }
    public string? RequestedBy { get; set; }
}
