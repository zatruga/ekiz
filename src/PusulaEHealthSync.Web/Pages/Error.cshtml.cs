using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PusulaEHealthSync.Web.Pages;

// KULLANICI ISTEGI (2026-08-28): "hata analizi yapan bir sistem olmalı bana doğrudan
// söylesin" -- eskiden bu sayfa yakalanan exception'a hic bakmiyordu, sadece varsayilan
// ASP.NET Core sablonundaki genel ingilizce metni gosteriyordu. Kullanici her hatada
// once Development moduna gecip/stdout log acip bana yapistirmak zorunda kaliyordu.
// Artik gercek Exception.Message (+ istenirse stack trace) dogrudan ekranda -- sayfa
// zaten AuthorizeFolder("/") ile girisli kullanicilara kapali oldugu icin (bkz.
// Program.cs) bunu gostermek disariya bilgi sizdirmiyor.
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; set; }
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }
    public string? ExceptionPath { get; set; }
    public string? ExceptionDetail { get; set; }

    public void OnGet()
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        if (feature?.Error is { } ex)
        {
            ExceptionType = ex.GetType().Name;
            ExceptionMessage = ex.Message;
            ExceptionPath = feature.Path;
            ExceptionDetail = ex.ToString();
        }
    }
}
