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
    private const string CheckTriggerFile = "check.trigger";
    private const string StatusFile = "update-status.json";
    private const string CheckFile = "update-check.json";
    private const string HistoryFile = "update-history.json";

    public UpdateStatusView? Durum { get; set; }
    public RemoteCheckView? Kontrol { get; set; }
    public List<UpdateStatusView> Gecmis { get; set; } = [];

    // KULLANICI ISTEGI (2026-08-27): "1 dakikada bir versiyon güncellemeyi kontrol
    // etmesin ... sürüm güncelle butonuna basınca otomatik kontrol etsin sayfa açılırken"
    // -- Updater artik periyodik/otonom GitHub kontrolu YAPMIYOR (bkz. UpdateWatcherService),
    // bu yuzden sayfa her acildiginda (F5 dahil) taze bir kontrol talebi birakiyoruz. Zaten
    // bir guncelleme/geri-alma suruyorsa (InProgress) tetiklemiyoruz -- diger tetikleyicilerle
    // ayni guard.
    public void OnGet()
    {
        Durum = OkuDurum();
        Kontrol = OkuKontrol();
        Gecmis = OkuGecmis();

        if (Durum?.State != "InProgress")
        {
            Directory.CreateDirectory(options.ControlPath);
            System.IO.File.WriteAllText(Path.Combine(options.ControlPath, CheckTriggerFile), "sayfa-acilisi");
        }
    }

    // Ana buton "güncel sürüm/kontrol edilmemiş" durumunda GUNCELLEME degil, sadece bir
    // kontrol talebi birakir (bkz. Guncelle.cshtml'de k?.HasUpdate'e gore buton metni/hedefi
    // degisiyor). Yikici bir islem olmadigi icin (sadece git fetch) burada onay penceresi yok.
    public IActionResult OnPostKontrolEtAsync()
    {
        if (OkuDurum()?.State != "InProgress")
        {
            Directory.CreateDirectory(options.ControlPath);
            System.IO.File.WriteAllText(Path.Combine(options.ControlPath, CheckTriggerFile), "manuel");
        }
        return RedirectToPage();
    }

    public IActionResult OnPostGuncelleAsync()
    {
        // Islem zaten surerken ikinci bir tetikleyici yazmayi engeller -- kullanici emin
        // olamayip birden fazla kez tikleyince (canli olayda gorulen davranis) gereksiz
        // bir ikinci kesinti dongusu baslamasin diye.
        if (OkuDurum()?.State != "InProgress")
        {
            Directory.CreateDirectory(options.ControlPath);
            System.IO.File.WriteAllText(Path.Combine(options.ControlPath, UpdateTriggerFile), User.Identity?.Name ?? "bilinmiyor");
        }
        return RedirectToPage();
    }

    public IActionResult OnPostGeriAlAsync()
    {
        if (OkuDurum()?.State != "InProgress")
        {
            Directory.CreateDirectory(options.ControlPath);
            System.IO.File.WriteAllText(Path.Combine(options.ControlPath, RollbackTriggerFile), User.Identity?.Name ?? "bilinmiyor");
        }
        return RedirectToPage();
    }

    public JsonResult OnGetDurum() => new(OkuDurum());

    public JsonResult OnGetKontrol() => new(OkuKontrol());

    public JsonResult OnGetGecmis() => new(OkuGecmis());

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

    private List<UpdateStatusView> OkuGecmis()
    {
        var path = Path.Combine(options.ControlPath, HistoryFile);
        if (!System.IO.File.Exists(path)) return [];
        try
        {
            var json = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<UpdateStatusView>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private RemoteCheckView? OkuKontrol()
    {
        var path = Path.Combine(options.ControlPath, CheckFile);
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            var json = System.IO.File.ReadAllText(path);
            return JsonSerializer.Deserialize<RemoteCheckView>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
    public string? CommitHash { get; set; }
    public string? CommitMessage { get; set; }
}

public class RemoteCheckView
{
    public string? DeployedCommitHash { get; set; }
    public List<PendingCommitView> PendingCommits { get; set; } = [];
    public bool HasUpdate { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    public string? Error { get; set; }
}

public class PendingCommitView
{
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
}
