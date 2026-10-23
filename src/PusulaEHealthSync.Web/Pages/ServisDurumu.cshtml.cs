using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
using PusulaEHealthSync.Web.Config;

namespace PusulaEHealthSync.Web.Pages;

// SERVIS DURUMU (2026-10-23, kullanici istegi: "servisi yeniden baslatma ve servis
// durumunu gosteren bir panel olmali").
//
// IKI KAYNAK, BILEREK:
//   - Bekcinin dosyasi (servis-durum.json): Windows'un gordugu durum, baslangic turu, son
//     yeniden baslatma, son uyari. Bunu YALNIZCA guncelleyici bilebilir -- Web'in IIS
//     hesabi Windows servislerini sorgulayamaz.
//   - Ayarlar (SettingsStore): nabiz, son tur, tur ilerlemesi. Gonderim servisi yaziyor;
//     bekci calismasa da (eski surum, durmus) bu kisim dogru kalir.
// Sayfa ikisini yan yana gosteriyor: bekci olu olsa bile "gonderim calisiyor mu" sorusunun
// cevabi ekranda.
//
// YENIDEN BASLATMA DOGRUDAN YAPILMIYOR: tetikleyici dosya birakiliyor, guncelleyici 30 sn
// icinde isliyor (bkz. ServisBekciService). Guncelle sayfasiyla ayni kanal ve gerekce.
public class ServisDurumuModel(
    IOptions<DeployOptions> deployOptions,
    SettingsStore settings,
    MailAyarlari mailAyarlari) : PageModel
{
    private readonly DeployOptions deploy = deployOptions.Value;

    public ServisDurumu? Bekci { get; private set; }

    // Bekcinin son kontrolu 3 dk'dan eskiyse bekci calismiyor demektir (30 sn'de bir yaziyor).
    public bool BekciCanli => Bekci is { } b && DateTime.UtcNow - b.KontrolUtc < TimeSpan.FromMinutes(3);
    public bool KontrolKlasoruVar => !string.IsNullOrWhiteSpace(deploy.ControlPath);

    public DateTime? NabizUtc { get; private set; }
    public int AralikDakika { get; private set; }
    public bool NabizBayat => ServisDurumu.NabizBayat(NabizUtc, AralikDakika, DateTime.UtcNow);
    public bool OtomatikAcik { get; private set; }
    public DateTime? SonTurUtc { get; private set; }
    public string SonTurOzet { get; private set; } = "";
    public int TurHedef { get; private set; }
    public int TurIslenen { get; private set; }
    public int TurNo { get; private set; }
    public bool Canli { get; private set; }

    public MailAyarlari.TurAyari? UyariAyari { get; private set; }

    [TempData] public string? IslemMesaji { get; set; }
    [TempData] public bool IslemBasarili { get; set; }

    // Yeniden baslatma istegi bekliyor mu? (tetikleyici henuz islenmedi)
    public bool IstekBekliyor => KontrolKlasoruVar
        && System.IO.File.Exists(Path.Combine(deploy.ControlPath, ServisDurumu.YenidenBaslatTetikleyici));

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (KontrolKlasoruVar) Bekci = ServisDurumu.Oku(deploy.ControlPath);

        static DateTime? Zaman(string s) => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : null;

        NabizUtc = Zaman(await settings.GetStringAsync(SettingsStore.AutoSendNabizUtcKey, "", ct));
        AralikDakika = await settings.GetIntAsync(SettingsStore.AutoSendIntervalMinutesKey,
            SettingsStore.AutoSendIntervalMinutesDefault, ct);
        OtomatikAcik = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
        SonTurUtc = Zaman(await settings.GetStringAsync(SettingsStore.AutoSendLastRunUtcKey, "", ct));
        SonTurOzet = await settings.GetStringAsync(SettingsStore.AutoSendLastRunOzetKey, "", ct);
        TurHedef = await settings.GetIntAsync(SettingsStore.AutoSendTurHedefKey, 0, ct);
        TurIslenen = await settings.GetIntAsync(SettingsStore.AutoSendTurIslenenKey, 0, ct);
        TurNo = await settings.GetIntAsync(SettingsStore.AutoSendTurNoKey, 0, ct);
        Canli = await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey,
            SettingsStore.EHealthEnvironmentDefault, ct) == "Live";
        UyariAyari = await mailAyarlari.OkuAsync(MailTurleri.ServisDurdu, ct);
    }

    public IActionResult OnPostYenidenBaslat()
    {
        if (!KontrolKlasoruVar)
        {
            IslemBasarili = false;
            IslemMesaji = "Kontrol klasörü tanımlı değil (Deploy:ControlPath) -- bu ortamda servis yönetilemez.";
            return RedirectToPage();
        }

        Directory.CreateDirectory(deploy.ControlPath);
        System.IO.File.WriteAllText(Path.Combine(deploy.ControlPath, ServisDurumu.YenidenBaslatTetikleyici),
            User.Identity?.Name ?? "bilinmiyor");
        IslemBasarili = true;
        IslemMesaji = "Yeniden başlatma isteği iletildi. Güncelleyici servisi en geç 30 saniye içinde işleyecek; "
                    + "sonuç aşağıda \"Son yeniden başlatma\" satırında görünecek.";
        return RedirectToPage();
    }
}
