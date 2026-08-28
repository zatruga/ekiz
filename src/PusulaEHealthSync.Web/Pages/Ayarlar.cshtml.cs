using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Ayarlar sayfasi kartlara bolunmus, her kart kendi POST handler'ina sahip -- boylece bir
// karti kaydetmek digerlerini de gondermeyi/gecerlemeyi gerektirmiyor (bkz. asp-page-handler
// her formda). Epikriz kurallari YAZILDI (2026-08-20, bkz. CompositionSyncService/
// SettingsStore.EpikrizSendEnabledKey). Lab hala "hazir degil" placeholder -- mapper
// yazilmadan bu alanin kaydedilmesinin bir anlami yok, bu yuzden formsuz.
public class AyarlarModel(SettingsStore settings) : PageModel
{
    public bool Saved { get; set; }
    public string? SavedSection { get; set; }

    // -- Kapanmamis protokol kurali -----------------------------------------------------
    [BindProperty]
    [Range(0, 365, ErrorMessage = "0 ile 365 arasında bir gün değeri girin.")]
    public int OpenProtokolSendAfterDays { get; set; }

    // -- Kaynak veritabani baglantisi -------------------------------------------------------
    // KULLANICI ISTEGI (2026-08-28): "connection string şeklinde yazmayalım, db ip, db adı,
    // kullanıcı adı şeklinde olsun" -- Sunucu/Veritabani/Kullanici acik metin (her acilista
    // gosterilir, digerlerinden farkli), Sifre ise diger sifre alanlariyla AYNI kalip (bos =
    // degistirme, tanimli mi bilgisi ayri gosteriliyor).
    [BindProperty]
    public string PusulaDbServer { get; set; } = "";
    [BindProperty]
    public string PusulaDbName { get; set; } = "";
    [BindProperty]
    public string PusulaDbUser { get; set; } = "";
    [BindProperty]
    public string PusulaDbPassword { get; set; } = "";
    public bool PusulaDbPasswordIsSet { get; set; }

    // -- Ortam / Endpoint -----------------------------------------------------------------
    [BindProperty]
    public string EHealthEnvironment { get; set; } = SettingsStore.EHealthEnvironmentDefault;
    [BindProperty]
    public string TestBaseUrl { get; set; } = "";
    [BindProperty]
    public string TestUserName { get; set; } = "";
    [BindProperty]
    public string TestPassword { get; set; } = "";
    [BindProperty]
    public string TestProviderId { get; set; } = "";
    [BindProperty]
    public string LiveBaseUrl { get; set; } = "";
    [BindProperty]
    public string LiveUserName { get; set; } = "";
    [BindProperty]
    public string LivePassword { get; set; } = "";
    [BindProperty]
    public string LiveProviderId { get; set; } = "";
    public bool TestPasswordIsSet { get; set; }
    public bool LivePasswordIsSet { get; set; }

    // -- Otomatik gonderim (genel) --------------------------------------------------------
    [BindProperty]
    public bool AutoSendPatientEnabled { get; set; }
    [BindProperty]
    public bool AutoSendEncounterEnabled { get; set; }
    [BindProperty]
    [Range(5, 1440)]
    public int AutoSendIntervalMinutes { get; set; }
    [BindProperty]
    [Range(1, 500)]
    public int AutoSendBatchSize { get; set; }

    // -- Hata sonrasi tekrar deneme --------------------------------------------------------
    [BindProperty]
    [Range(1, 1440)]
    public int RetryIntervalMinutes { get; set; }
    [BindProperty]
    [Range(1, 50)]
    public int RetryMaxAttempts { get; set; }

    // -- Epikriz (Composition) -------------------------------------------------------------
    [BindProperty]
    public bool EpikrizSendEnabled { get; set; }
    [BindProperty]
    public bool EpikrizOnlySigned { get; set; }

    // -- Tanı (Condition) / İşlem (Procedure) ------------------------------------------------
    [BindProperty]
    public bool ConditionSendEnabled { get; set; }
    [BindProperty]
    public bool ProcedureSendEnabled { get; set; }

    // -- Gunluk e-posta raporu -------------------------------------------------------------
    [BindProperty]
    public bool MailEnabled { get; set; }
    [BindProperty]
    public string MailSmtpHost { get; set; } = "";
    [BindProperty]
    [Range(1, 65535)]
    public int MailSmtpPort { get; set; }
    [BindProperty]
    public bool MailUseTls { get; set; }
    [BindProperty]
    public string MailUsername { get; set; } = "";
    [BindProperty]
    public string MailPassword { get; set; } = "";
    [BindProperty]
    [EmailAddress]
    public string MailFromAddress { get; set; } = "";
    [BindProperty]
    [Range(0, 23)]
    public int MailSendHour { get; set; }
    [BindProperty]
    public string MailRecipients { get; set; } = "";

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostVeritabaniAsync(CancellationToken ct)
    {
        await settings.SetStringAsync(SettingsStore.PusulaDbServerKey, Clean(PusulaDbServer), ct);
        await settings.SetStringAsync(SettingsStore.PusulaDbNameKey, Clean(PusulaDbName), ct);
        await settings.SetStringAsync(SettingsStore.PusulaDbUserKey, Clean(PusulaDbUser), ct);
        await SetPasswordIfProvidedAsync(SettingsStore.PusulaDbPasswordKey, PusulaDbPassword, ct);
        return await SavedAsync("veritabani", ct);
    }

    public async Task<IActionResult> OnPostProtokolAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) { await LoadAsync(ct, skipProtokol: true); return Page(); }
        await settings.SetIntAsync(SettingsStore.OpenProtokolSendAfterDaysKey, OpenProtokolSendAfterDays, ct);
        return await SavedAsync("protokol", ct);
    }

    // DUZELTME (2026-08-21, canli olayda bulundu): Sifre alanlari <input type="password">
    // -- ASP.NET Core'un InputTagHelper'i GUVENLIK GEREGI bu tur alanlara asp-for ile bile
    // deger basmiyor, yani sayfa her acildiginda BOS gorunuyor (saklanmis sifre olsa bile).
    // Eskiden bu form "bos = sifreyi bosalt" olarak davraniyordu -- kullanici sadece Ortam
    // (Test/Canli) radyo butonunu degistirip Kaydet'e bassa bile, sifre alanlarini elle
    // yeniden yazmadigi surece saklanmis CANLI SIFRESI sessizce siliniyordu (production auth
    // 401 "Failed to authenticate" ile sonuclandi, kok neden BUYDU). Artik sifre alanlari
    // SADECE doldurulmus gonderilirse guncelleniyor -- bos birakmak "degistirme" anlamina
    // geliyor, digerlerinden (BaseUrl/UserName/ProviderId, kasitli bosaltilabilir) farkli.
    public async Task<IActionResult> OnPostOrtamAsync(CancellationToken ct)
    {
        await settings.SetStringAsync(SettingsStore.EHealthEnvironmentKey, EHealthEnvironment == "Live" ? "Live" : "Test", ct);
        await settings.SetStringAsync(SettingsStore.EHealthTestBaseUrlKey, Clean(TestBaseUrl), ct);
        await settings.SetStringAsync(SettingsStore.EHealthTestUserNameKey, Clean(TestUserName), ct);
        await SetPasswordIfProvidedAsync(SettingsStore.EHealthTestPasswordKey, TestPassword, ct);
        await settings.SetStringAsync(SettingsStore.EHealthTestProviderIdKey, Clean(TestProviderId), ct);
        await settings.SetStringAsync(SettingsStore.EHealthLiveBaseUrlKey, Clean(LiveBaseUrl), ct);
        await settings.SetStringAsync(SettingsStore.EHealthLiveUserNameKey, Clean(LiveUserName), ct);
        await SetPasswordIfProvidedAsync(SettingsStore.EHealthLivePasswordKey, LivePassword, ct);
        await settings.SetStringAsync(SettingsStore.EHealthLiveProviderIdKey, Clean(LiveProviderId), ct);
        return await SavedAsync("ortam", ct);
    }

    private async Task SetPasswordIfProvidedAsync(string key, string? submittedValue, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(submittedValue))
            await settings.SetStringAsync(key, submittedValue, ct);
    }

    public async Task<IActionResult> OnPostGenelAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) { await LoadAsync(ct, skipGenel: true); return Page(); }
        await settings.SetBoolAsync(SettingsStore.AutoSendPatientEnabledKey, AutoSendPatientEnabled, ct);
        await settings.SetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, AutoSendEncounterEnabled, ct);
        await settings.SetIntAsync(SettingsStore.AutoSendIntervalMinutesKey, AutoSendIntervalMinutes, ct);
        await settings.SetIntAsync(SettingsStore.AutoSendBatchSizeKey, AutoSendBatchSize, ct);
        return await SavedAsync("genel", ct);
    }

    public async Task<IActionResult> OnPostTekrarAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) { await LoadAsync(ct, skipTekrar: true); return Page(); }
        await settings.SetIntAsync(SettingsStore.RetryIntervalMinutesKey, RetryIntervalMinutes, ct);
        await settings.SetIntAsync(SettingsStore.RetryMaxAttemptsKey, RetryMaxAttempts, ct);
        return await SavedAsync("tekrar", ct);
    }

    public async Task<IActionResult> OnPostEpikrizAsync(CancellationToken ct)
    {
        await settings.SetBoolAsync(SettingsStore.EpikrizSendEnabledKey, EpikrizSendEnabled, ct);
        await settings.SetBoolAsync(SettingsStore.EpikrizOnlySignedKey, EpikrizOnlySigned, ct);
        return await SavedAsync("epikriz", ct);
    }

    public async Task<IActionResult> OnPostTaniIslemAsync(CancellationToken ct)
    {
        await settings.SetBoolAsync(SettingsStore.ConditionSendEnabledKey, ConditionSendEnabled, ct);
        await settings.SetBoolAsync(SettingsStore.ProcedureSendEnabledKey, ProcedureSendEnabled, ct);
        return await SavedAsync("tanislem", ct);
    }

    public async Task<IActionResult> OnPostMailAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) { await LoadAsync(ct, skipMail: true); return Page(); }
        await settings.SetBoolAsync(SettingsStore.MailEnabledKey, MailEnabled, ct);
        await settings.SetStringAsync(SettingsStore.MailSmtpHostKey, Clean(MailSmtpHost), ct);
        await settings.SetIntAsync(SettingsStore.MailSmtpPortKey, MailSmtpPort, ct);
        await settings.SetBoolAsync(SettingsStore.MailUseTlsKey, MailUseTls, ct);
        await settings.SetStringAsync(SettingsStore.MailUsernameKey, Clean(MailUsername), ct);
        await settings.SetStringAsync(SettingsStore.MailPasswordKey, MailPassword ?? "", ct);
        await settings.SetStringAsync(SettingsStore.MailFromAddressKey, Clean(MailFromAddress), ct);
        await settings.SetIntAsync(SettingsStore.MailSendHourKey, MailSendHour, ct);
        await settings.SetStringAsync(SettingsStore.MailRecipientsKey, Clean(MailRecipients), ct);
        return await SavedAsync("mail", ct);
    }

    // Bos birakilan (opsiyonel) alanlar icin -- ASP.NET Core model binding, formda bos
    // gonderilen bir string alanini "" degil NULL'a bagliyor (canli testte
    // NullReferenceException ile ortaya cikti); Trim() cagirmadan once bunu guvenli hale
    // getirir.
    private static string Clean(string? s) => (s ?? "").Trim();

    private async Task<IActionResult> SavedAsync(string section, CancellationToken ct)
    {
        await LoadAsync(ct);
        Saved = true;
        SavedSection = section;
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct,
        bool skipProtokol = false, bool skipGenel = false, bool skipTekrar = false, bool skipMail = false)
    {
        if (!skipProtokol)
            OpenProtokolSendAfterDays = await settings.GetIntAsync(SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);

        PusulaDbServer = await settings.GetStringAsync(SettingsStore.PusulaDbServerKey, "", ct);
        PusulaDbName = await settings.GetStringAsync(SettingsStore.PusulaDbNameKey, "", ct);
        PusulaDbUser = await settings.GetStringAsync(SettingsStore.PusulaDbUserKey, "", ct);
        PusulaDbPasswordIsSet = !string.IsNullOrWhiteSpace(await settings.GetStringAsync(SettingsStore.PusulaDbPasswordKey, "", ct));

        EHealthEnvironment = await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);
        TestBaseUrl = await settings.GetStringAsync(SettingsStore.EHealthTestBaseUrlKey, "", ct);
        TestUserName = await settings.GetStringAsync(SettingsStore.EHealthTestUserNameKey, "", ct);
        TestProviderId = await settings.GetStringAsync(SettingsStore.EHealthTestProviderIdKey, "", ct);
        LiveBaseUrl = await settings.GetStringAsync(SettingsStore.EHealthLiveBaseUrlKey, "", ct);
        LiveUserName = await settings.GetStringAsync(SettingsStore.EHealthLiveUserNameKey, "", ct);
        LiveProviderId = await settings.GetStringAsync(SettingsStore.EHealthLiveProviderIdKey, "", ct);
        // Sifreler BILEREK bound property'lere yuklenmiyor -- <input type="password"> zaten
        // gostermiyor, sunucu tarafinda bile gereksiz yere tutmamak icin sadece "tanimli mi"
        // bilgisi gonderiliyor (bkz. OnPostOrtamAsync'teki DUZELTME notu).
        TestPasswordIsSet = !string.IsNullOrWhiteSpace(await settings.GetStringAsync(SettingsStore.EHealthTestPasswordKey, "", ct));
        LivePasswordIsSet = !string.IsNullOrWhiteSpace(await settings.GetStringAsync(SettingsStore.EHealthLivePasswordKey, "", ct));

        if (!skipGenel)
        {
            AutoSendPatientEnabled = await settings.GetBoolAsync(SettingsStore.AutoSendPatientEnabledKey, false, ct);
            AutoSendEncounterEnabled = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
            AutoSendIntervalMinutes = await settings.GetIntAsync(SettingsStore.AutoSendIntervalMinutesKey, SettingsStore.AutoSendIntervalMinutesDefault, ct);
            AutoSendBatchSize = await settings.GetIntAsync(SettingsStore.AutoSendBatchSizeKey, SettingsStore.AutoSendBatchSizeDefault, ct);
        }

        if (!skipTekrar)
        {
            RetryIntervalMinutes = await settings.GetIntAsync(SettingsStore.RetryIntervalMinutesKey, SettingsStore.RetryIntervalMinutesDefault, ct);
            RetryMaxAttempts = await settings.GetIntAsync(SettingsStore.RetryMaxAttemptsKey, SettingsStore.RetryMaxAttemptsDefault, ct);
        }

        EpikrizSendEnabled = await settings.GetBoolAsync(SettingsStore.EpikrizSendEnabledKey, true, ct);
        EpikrizOnlySigned = await settings.GetBoolAsync(SettingsStore.EpikrizOnlySignedKey, true, ct);

        ConditionSendEnabled = await settings.GetBoolAsync(SettingsStore.ConditionSendEnabledKey, true, ct);
        ProcedureSendEnabled = await settings.GetBoolAsync(SettingsStore.ProcedureSendEnabledKey, true, ct);

        if (!skipMail)
        {
            MailEnabled = await settings.GetBoolAsync(SettingsStore.MailEnabledKey, false, ct);
            MailSmtpHost = await settings.GetStringAsync(SettingsStore.MailSmtpHostKey, SettingsStore.MailSmtpHostDefault, ct);
            MailSmtpPort = await settings.GetIntAsync(SettingsStore.MailSmtpPortKey, SettingsStore.MailSmtpPortDefault, ct);
            MailUseTls = await settings.GetBoolAsync(SettingsStore.MailUseTlsKey, false, ct);
            MailUsername = await settings.GetStringAsync(SettingsStore.MailUsernameKey, "", ct);
            MailPassword = await settings.GetStringAsync(SettingsStore.MailPasswordKey, "", ct);
            MailFromAddress = await settings.GetStringAsync(SettingsStore.MailFromAddressKey, SettingsStore.MailFromAddressDefault, ct);
            MailSendHour = await settings.GetIntAsync(SettingsStore.MailSendHourKey, SettingsStore.MailSendHourDefault, ct);
            MailRecipients = await settings.GetStringAsync(SettingsStore.MailRecipientsKey, "", ct);
        }
    }
}
