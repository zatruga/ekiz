using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// Ayarlar sayfasi kartlara bolunmus, her kart kendi POST handler'ina sahip -- boylece bir
// karti kaydetmek digerlerini de gondermeyi/gecerlemeyi gerektirmiyor (bkz. asp-page-handler
// her formda). Epikriz kurallari YAZILDI (2026-08-20, bkz. CompositionSyncService/
// SettingsStore.EpikrizSendEnabledKey). Lab hala "hazir degil" placeholder -- mapper
// yazilmadan bu alanin kaydedilmesinin bir anlami yok, bu yuzden formsuz.
public class AyarlarModel(
    SettingsStore settings,
    GunSonuRaporService gunSonuRapor,
    MailSender mailSender,
    ILogger<AyarlarModel> logger) : PageModel
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

    // -- Bekleyen is taramasi araligi -------------------------------------------------------
    // KULLANICI ISTEGI (2026-09-28): tarama "tum data" olmasin; ya hazir bir aralik
    // (son 1 ay / son 3 ay ...) ya da girilen bir tarihten itibaren olsun.
    [BindProperty]
    public string PendingScanMode { get; set; } = SettingsStore.PendingScanModeDefault;
    [BindProperty]
    [Range(1, 3650)]
    public int PendingScanPresetDays { get; set; }
    [BindProperty]
    [DataType(DataType.Date)]
    public DateTime? PendingScanFromDate { get; set; }

    // Artimli tarama: saatlik tur yukaridaki pencerenin TAMAMINI degil, son taramadan
    // beri tamamlananlari sorar. Ust sinir yine yukaridaki pencere.
    [BindProperty]
    public bool PendingScanIncrementalEnabled { get; set; }
    [BindProperty]
    [Range(0, 23)]
    public int PendingScanFullSweepHour { get; set; }

    // Ekrandaki "su an sunu tariyor" ozeti -- secim kaydedildikten sonra ne olacagini
    // kullanicinin tahmin etmesi gerekmesin.
    public DateTime PendingScanEffectiveFrom { get; private set; }

    public static readonly (int Gun, string Ad)[] TaramaHazirAraliklar =
    [
        (7, "Son 1 hafta"),
        (14, "Son 2 hafta"),
        (30, "Son 1 ay"),
        (90, "Son 3 ay"),
        (180, "Son 6 ay"),
        (365, "Son 1 yıl"),
    ];

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

    // Tarama araligi. Iki mod tek formda: hazir aralik (gun sayisi) ya da belirli tarih.
    // Secilmeyen modun alani DOGRULANMAZ -- ornegin hazir aralik seciliyken bos birakilmis
    // bir tarih kutusu formu gecersiz kilmamali.
    public async Task<IActionResult> OnPostTaramaAsync(CancellationToken ct)
    {
        var tarihModu = string.Equals(PendingScanMode, "Date", StringComparison.OrdinalIgnoreCase);

        if (tarihModu)
        {
            ModelState.Remove(nameof(PendingScanPresetDays));
            if (PendingScanFromDate is null)
                ModelState.AddModelError(nameof(PendingScanFromDate), "Bir başlangıç tarihi seçin.");
            else if (PendingScanFromDate.Value.Date > DateTime.Now.Date)
                ModelState.AddModelError(nameof(PendingScanFromDate), "Başlangıç tarihi gelecekte olamaz.");
        }
        else
        {
            ModelState.Remove(nameof(PendingScanFromDate));
        }

        if (!ModelState.IsValid) { await LoadAsync(ct, skipTarama: true); return Page(); }

        await settings.SetStringAsync(SettingsStore.PendingScanModeKey, tarihModu ? "Date" : "Preset", ct);
        if (tarihModu)
            await settings.SetStringAsync(SettingsStore.PendingScanFromDateKey,
                PendingScanFromDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        else
            await settings.SetIntAsync(SettingsStore.PendingScanPresetDaysKey, PendingScanPresetDays, ct);

        await settings.SetBoolAsync(
            SettingsStore.PendingScanIncrementalEnabledKey, PendingScanIncrementalEnabled, ct);
        await settings.SetIntAsync(
            SettingsStore.PendingScanFullSweepHourKey, PendingScanFullSweepHour, ct);

        return await SavedAsync("tarama", ct);
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


    // ---- OTOMATIK GONDERIM DURUMU (2026-10-03) --------------------------------------
    // KULLANICI ISTEGI: "ayarlar kısmında otomatik gönderimde altına otomatik gönderimi
    // başlat seçeneği olmalı."
    //
    // Baslat/Durdur AYRI BIR FORM: ayar kaydetmekten farkli bir is. Eskiden anahtar, araliк
    // ve parti kutulariyla ayni "Kaydet" dugmesine bagliydi -- yani canli veri yazan bir
    // donguyu baslatmak, bir sayiyi degistirmekle ayni jestti. Artik kendi dugmesi, kendi
    // onayi ve ortam uyarisi var.
    public bool OtomatikCalisiyor { get; private set; }
    public string Ortam { get; private set; } = "Test";
    public DateTime? SonTurUtc { get; private set; }
    public string? SonTurOzet { get; private set; }

    [BindProperty]
    [Range(0, 30)]
    public int MinProtokolYasiGun { get; set; }
    [BindProperty]
    [Range(0, 23)]
    public int TakilanDenemeSaati { get; set; }
    // SAYI DEGIL SURE (2026-10-05): takilanlar turu artik "kac protokol" ile degil "kac
    // dakika" ile siniriliyor -- gerekce SettingsStore.StuckRetryMaxMinutesKey'de.
    [BindProperty]
    [Range(5, 300)]
    public int TakilanSureDakika { get; set; }

    // Dongunun KENDISINI acip kapatir -- ayar kaydetmez. Onay metni ortami ve parti
    // boyutunu acikca yaziyor (bkz. Ayarlar.cshtml).
    //
    // IKI AYRI HANDLER, tek handler + "baslat" parametresi DEGIL: handler parametresi form
    // alanindan baglanmazsa sessizce varsayilan degere (false) duser, yani "Başlat"
    // dugmesi hicbir sey yapmaz ya da tam tersini yapar -- ekranda hicbir belirti olmadan.
    // Canli veri yazan bir donguyu acip kapatan kontrolde bu belirsizlige yer yok.
    public Task<IActionResult> OnPostBaslatAsync(CancellationToken ct) => OtomatikAyarlaAsync(true, ct);
    public Task<IActionResult> OnPostDurdurAsync(CancellationToken ct) => OtomatikAyarlaAsync(false, ct);

    private async Task<IActionResult> OtomatikAyarlaAsync(bool acik, CancellationToken ct)
    {
        await settings.SetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, acik, ct);
        logger.LogWarning("Otomatik gonderim {Durum} (Ayarlar sayfasindan).", acik ? "BASLATILDI" : "DURDURULDU");
        return await SavedAsync("genel", ct);
    }

    public async Task<IActionResult> OnPostGenelAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) { await LoadAsync(ct, skipGenel: true); return Page(); }
        // AutoSend.Patient.Enabled ARTIK YAZILMIYOR (2026-10-03): o kutu kaydediliyordu ama
        // HICBIR SEY okumuyordu -- hasta zaten protokol zincirinin parcasi olarak gidiyor.
        // Arkasinda calisan bir sey olmayan bir anahtar, kullaniciya yanlis bir kontrol
        // duygusu veriyordu; kutu ekrandan da kaldirildi.
        //
        // Dongunun acma/kapama anahtari da burada DEGIL: kendi dugmesi ve onayi var
        // (bkz. OnPostOtomatikAsync). Canli veri yazan bir donguyu baslatmak, bir sayiyi
        // degistirmekle ayni jest olmamali.
        await settings.SetIntAsync(SettingsStore.AutoSendIntervalMinutesKey, AutoSendIntervalMinutes, ct);
        await settings.SetIntAsync(SettingsStore.AutoSendBatchSizeKey, AutoSendBatchSize, ct);
        await settings.SetIntAsync(SettingsStore.MinProtokolYasiGunKey, MinProtokolYasiGun, ct);
        await settings.SetIntAsync(SettingsStore.StuckRetryHourKey, TakilanDenemeSaati, ct);
        await settings.SetIntAsync(SettingsStore.StuckRetryMaxMinutesKey, TakilanSureDakika, ct);
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

    // ---- GUN SONU MAILI TEST GONDERIMI (2026-10-05) ---------------------------------
    // NEDEN GEREKLI: SMTP yapilandirmasi yanlissa bunu sabah 07:00'de, rapor gitmedigi
    // icin ogrenirdik -- ve kimse fark etmezdi, cunku "mail gelmedi" ile "sorun yok"
    // birbirinden ayirt edilemez. Test dugmesi bu belirsizligi aninda cozuyor.
    //
    // AYRI HANDLER, AYRI FORM: ustteki Kaydet'e baglansaydi bir ayari degistirmek
    // istedigimizde mail de giderdi. Ayni gerekce otomatik gonderim Baslat/Durdur
    // dugmelerinde de uygulandi.
    public string? MailTestSonucu { get; private set; }
    public bool MailTestBasarili { get; private set; }

    public async Task<IActionResult> OnPostTestMailiAsync(CancellationToken ct)
    {
        try
        {
            // Icerik GERCEK rapor, uydurma bir ornek degil: SMTP'nin calistigini
            // kanitlamanin yani sira raporun kendi bicimi de gorulmus olsun.
            var rapor = await gunSonuRapor.OlusturAsync(DateOnly.FromDateTime(DateTime.Now.AddDays(-1)), ct);
            var sonuc = await mailSender.GonderAsync(
                "[TEST] " + GunSonuMailHtml.Konu(rapor), GunSonuMailHtml.Govde(rapor), ct);

            MailTestBasarili = sonuc.Basarili;
            MailTestSonucu = sonuc.Mesaj;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Gun sonu maili test gonderimi basarisiz.");
            MailTestBasarili = false;
            MailTestSonucu = "Hata: " + ex.Message;
        }

        await LoadAsync(ct);
        SavedSection = "mail";
        return Page();
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
        bool skipProtokol = false, bool skipGenel = false, bool skipTekrar = false, bool skipMail = false,
        bool skipTarama = false)
    {
        if (!skipTarama)
        {
            PendingScanMode = await settings.GetStringAsync(
                SettingsStore.PendingScanModeKey, SettingsStore.PendingScanModeDefault, ct);
            PendingScanPresetDays = await settings.GetIntAsync(
                SettingsStore.PendingScanPresetDaysKey, SettingsStore.PendingScanPresetDaysDefault, ct);
            var hamTarih = await settings.GetStringAsync(SettingsStore.PendingScanFromDateKey, "", ct);
            PendingScanFromDate = DateTime.TryParseExact(
                hamTarih, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t : null;
            PendingScanIncrementalEnabled = await settings.GetBoolAsync(
                SettingsStore.PendingScanIncrementalEnabledKey, false, ct);
            PendingScanFullSweepHour = await settings.GetIntAsync(
                SettingsStore.PendingScanFullSweepHourKey, SettingsStore.PendingScanFullSweepHourDefault, ct);
        }
        // Ozet her zaman GERCEK ayardan hesaplanir (form dogrulamasi patlasa bile
        // kullaniciya "su an sunu tariyor" dogru gosterilsin).
        PendingScanEffectiveFrom = await PendingWorkService.TaramaBaslangiciAsync(settings, ct);

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
            AutoSendEncounterEnabled = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
            AutoSendIntervalMinutes = await settings.GetIntAsync(SettingsStore.AutoSendIntervalMinutesKey, SettingsStore.AutoSendIntervalMinutesDefault, ct);
            AutoSendBatchSize = await settings.GetIntAsync(SettingsStore.AutoSendBatchSizeKey, SettingsStore.AutoSendBatchSizeDefault, ct);
            MinProtokolYasiGun = await settings.GetIntAsync(SettingsStore.MinProtokolYasiGunKey, SettingsStore.MinProtokolYasiGunDefault, ct);
            TakilanDenemeSaati = await settings.GetIntAsync(SettingsStore.StuckRetryHourKey, SettingsStore.StuckRetryHourDefault, ct);
            TakilanSureDakika = await settings.GetIntAsync(SettingsStore.StuckRetryMaxMinutesKey, SettingsStore.StuckRetryMaxMinutesDefault, ct);
        }

        // Durum kutusu HER ZAMAN okunur (skipGenel olsa bile) -- kaydetme sonrasi sayfa
        // yeniden cizilirken durumun kaybolmamasi icin.
        {
            OtomatikCalisiyor = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
            Ortam = await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);
            SonTurOzet = await settings.GetStringAsync(SettingsStore.AutoSendLastRunOzetKey, "", ct);
            var sonMetin = await settings.GetStringAsync(SettingsStore.AutoSendLastRunUtcKey, "", ct);
            if (DateTime.TryParse(sonMetin, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var son))
                SonTurUtc = son;
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
