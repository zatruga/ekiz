using System.Net;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
using PusulaEHealthSync.Updater.Config;

namespace PusulaEHealthSync.Updater;

// GONDERIM SERVISININ BEKCISI (2026-10-23).
//
// KULLANICI ISTEGI: "servisi yeniden baslatma ve servis durumunu gosteren bir panel olmali;
// ayrica otomatik gonderim durduysa bunu uygulama acik olmasa bile bilmem gerekli, mail ile
// bildirebilir."
//
// NEDEN BURADA (guncelleyicide): olen bir gonderim servisi kendi olumunu haber veremez; Web
// ise IIS havuzu bos kalinca kendisi de kapaniyor -- gonderim tam bu yuzden gunlerce
// durmustu ve kimse fark etmemisti. Guncelleyici surekli calisan, servisleri zaten
// durdurup baslatan ayri bir Windows servisi. Web ile kontrol klasoru uzerinden konusuyor.
//
// HER DONGUDE:
//   1. Panelden "yeniden baslat" istegi geldiyse servisi yeniden baslatir.
//   2. Servisin Windows durumuna ve nabzina bakar, sonucu servis-durum.json'a yazar.
//   3. Sorun TOLERANS suresinden uzun surduyse e-posta atar; surdukce aralikla hatirlatir;
//      duzelince "tekrar calisiyor" e-postasi atar.
//
// GUNCELLEME SURERKEN SAYMAZ: guncelleyici servisi bilerek durduruyor; o bir ariza degil.
public class ServisBekciService(
    IOptions<DeployOptions> optionsAccessor,
    SettingsStore settings,
    MailSender mail,
    MailAyarlari mailAyarlari,
    ILogger<ServisBekciService> logger) : BackgroundService
{
    private readonly DeployOptions options = optionsAccessor.Value;

    // 30 sn: panelden gelen yeniden baslatma istegi bu kadar icinde islenir. Kontrolun
    // kendisi ucuz (bir servis sorgusu + ayar okumasi).
    private static readonly TimeSpan Aralik = TimeSpan.FromSeconds(30);

    private int _hatirlatma;
    private DateTime? _sorunBaslangic;
    private DateTime? _sonUyari;
    private string? _sonUyariSonucu;
    private bool _sorunMailiGitti;
    private ServisDurumu.YenidenBaslatma? _sonYenidenBaslatma;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.ControlPath);

        // Guncelleyici yeniden basladiginda uyari durumunu KAYBETMESIN: yoksa her
        // yeniden baslamada ayni sorun icin yeni bir "durdu" maili giderdi.
        if (ServisDurumu.Oku(options.ControlPath) is { } onceki)
        {
            _sorunBaslangic = onceki.SorunBaslangicUtc;
            _sonUyari = onceki.SonUyariUtc;
            _sonUyariSonucu = onceki.SonUyariSonucu;
            _sorunMailiGitti = onceki.SorunMailiGitti;
            _sonYenidenBaslatma = onceki.SonYenidenBaslatma;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await YenidenBaslatmaIsteginiIsleAsync(stoppingToken);
                await KontrolEtAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Bekci DONGUYU OLDURMEMELI -- guncelleme kanali da bu serviste.
                logger.LogError(ex, "Servis bekcisi kontrolunde hata.");
            }

            try { await Task.Delay(Aralik, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ------------------------------------------------------------------ YENIDEN BASLATMA

    private async Task YenidenBaslatmaIsteginiIsleAsync(CancellationToken ct)
    {
        var tetik = Path.Combine(options.ControlPath, ServisDurumu.YenidenBaslatTetikleyici);
        if (!File.Exists(tetik)) return;

        string? isteyen = null;
        try { isteyen = File.ReadAllText(tetik).Trim(); File.Delete(tetik); }
        catch (Exception ex) { logger.LogWarning(ex, "Yeniden baslatma tetikleyicisi okunamadi."); return; }

        if (GuncellemeSuruyor())
        {
            _sonYenidenBaslatma = new(DateTime.UtcNow, isteyen, false,
                "Güncelleme sürüyor -- servis güncelleme bitince zaten yeniden başlatılacak.");
            return;
        }

        logger.LogWarning("{Servis} yeniden baslatiliyor (isteyen: {Isteyen}).", options.WorkerServiceName, isteyen);
        _sonYenidenBaslatma = await Task.Run(() => YenidenBaslat(isteyen), ct);
    }

    private ServisDurumu.YenidenBaslatma YenidenBaslat(string? isteyen)
    {
        try
        {
            using var sc = new ServiceController(options.WorkerServiceName);
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return new(DateTime.UtcNow, isteyen, true, "Servis yeniden başlatıldı.");
        }
        catch (Exception ex)
        {
            // En olasi sebep YETKI: guncelleyicinin hesabinin bu servisi baslatma/durdurma
            // hakki yoksa burada "Access is denied" gorunur. Mesaj ekrana aynen gidiyor --
            // sessizce yutulursa kullanici dugmenin calistigini sanar.
            logger.LogError(ex, "{Servis} yeniden baslatilamadi.", options.WorkerServiceName);
            var ic = ex.InnerException?.Message;
            return new(DateTime.UtcNow, isteyen, false,
                $"Yeniden başlatılamadı: {ex.Message}{(ic is null ? "" : $" ({ic})")}");
        }
    }

    // ------------------------------------------------------------------ DURUM

    private async Task KontrolEtAsync(CancellationToken ct)
    {
        var simdi = DateTime.UtcNow;
        var (windowsDurumu, baslangicTuru) = ServisBilgisi();
        var guncelleme = GuncellemeSuruyor();

        DateTime? nabiz = null;
        bool otomatikAcik = false;
        int aralikDk = SettingsStore.AutoSendIntervalMinutesDefault;
        try
        {
            nabiz = DateTime.TryParse(await settings.GetStringAsync(SettingsStore.AutoSendNabizUtcKey, "", ct),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var n) ? n : null;
            otomatikAcik = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
            aralikDk = await settings.GetIntAsync(SettingsStore.AutoSendIntervalMinutesKey,
                SettingsStore.AutoSendIntervalMinutesDefault, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bekci: ayarlar okunamadi.");
        }

        string? sorun =
            windowsDurumu == "Bulunamadi" ? $"{options.WorkerServiceName} servisi bu sunucuda kurulu değil."
            : windowsDurumu != nameof(ServiceControllerStatus.Running) ? $"Servis çalışmıyor (Windows durumu: {windowsDurumu})."
            : ServisDurumu.NabizBayat(nabiz, aralikDk, simdi)
                ? nabiz is { } nb
                    ? $"Servis çalışıyor görünüyor ama {(int)(simdi - nb).TotalMinutes} dakikadır nabız yok -- takılmış olabilir."
                    : "Servis çalışıyor görünüyor ama hiç nabız yazmadı -- eski sürüm çalışıyor olabilir."
                : null;

        // Guncelleme sirasinda sayac DONAR: ne yeni sorun baslatilir ne de var olan
        // "duzeldi" sayilir -- guncelleme bitince durum yeniden degerlendirilir.
        if (!guncelleme)
        {
            if (sorun is null) await DuzeldiyseBildirAsync(simdi, ct);
            else await SorunVarsaBildirAsync(sorun, simdi, ct);
        }

        new ServisDurumu
        {
            KontrolUtc = simdi,
            ServisAdi = options.WorkerServiceName,
            WindowsDurumu = windowsDurumu,
            BaslangicTuru = baslangicTuru,
            NabizUtc = nabiz,
            OtomatikGonderimAcik = otomatikAcik,
            GuncellemeSuruyor = guncelleme,
            Saglikli = sorun is null,
            Sorun = sorun,
            SorunBaslangicUtc = _sorunBaslangic,
            SorunMailiGitti = _sorunMailiGitti,
            SonUyariUtc = _sonUyari,
            SonUyariSonucu = _sonUyariSonucu,
            SonYenidenBaslatma = _sonYenidenBaslatma,
        }.Yaz(options.ControlPath);
    }

    private (string Durum, string Baslangic) ServisBilgisi()
    {
        try
        {
            using var sc = new ServiceController(options.WorkerServiceName);
            sc.Refresh();
            return (sc.Status.ToString(), sc.StartType.ToString());
        }
        catch (InvalidOperationException)
        {
            return ("Bulunamadi", "");
        }
    }

    private bool GuncellemeSuruyor()
    {
        try
        {
            var yol = Path.Combine(options.ControlPath, "update-status.json");
            if (!File.Exists(yol)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(yol));
            return doc.RootElement.TryGetProperty("State", out var s) && s.GetString() == "InProgress";
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ E-POSTA

    private async Task SorunVarsaBildirAsync(string sorun, DateTime simdi, CancellationToken ct)
    {
        _sorunBaslangic ??= simdi;
        // ZAMANLAMA AYARDAN (Ayarlar → Mail → Gonderim durdu uyarisi).
        var tolerans = Math.Max(1, await settings.GetIntAsync(
            SettingsStore.UyariToleransDakikaKey, SettingsStore.UyariToleransDakikaDefault, ct));
        if (simdi - _sorunBaslangic < TimeSpan.FromMinutes(tolerans)) return;

        var tekrarSaat = Math.Max(1, await settings.GetIntAsync(
            SettingsStore.UyariTekrarSaatKey, SettingsStore.UyariTekrarSaatDefault, ct));
        if (_sorunMailiGitti && _sonUyari is { } son && simdi - son < TimeSpan.FromHours(tekrarSaat)) return;

        var degerler = new Dictionary<string, string>
        {
            ["sorun"] = sorun,
            ["baslangic"] = AzTime.ToLocal(_sorunBaslangic.Value).ToString("dd.MM.yyyy HH:mm"),
            ["sure"] = SureMetni(simdi - _sorunBaslangic.Value),
            ["servis"] = options.WorkerServiceName,
            ["hatirlatma"] = _hatirlatma.ToString(),
        };
        if (await GonderAsync(MailTurleri.ServisDurdu, degerler, simdi, ct))
        {
            if (_sorunMailiGitti) _hatirlatma++;
            _sorunMailiGitti = true;
        }
    }

    private async Task DuzeldiyseBildirAsync(DateTime simdi, CancellationToken ct)
    {
        if (_sorunBaslangic is not { } basl) return;

        // Uyari HIC gitmediyse (tolerans icinde duzeldi) "duzeldi" maili de gitmez --
        // haberi olmayan birine "duzeldi" demek gurultu.
        if (_sorunMailiGitti)
        {
            var degerler = new Dictionary<string, string>
            {
                ["baslangic"] = AzTime.ToLocal(basl).ToString("dd.MM.yyyy HH:mm"),
                ["bitis"] = AzTime.ToLocal(simdi).ToString("dd.MM.yyyy HH:mm"),
                ["sure"] = SureMetni(simdi - basl),
            };
            await GonderAsync(MailTurleri.ServisDuzeldi, degerler, simdi, ct);
        }

        _sorunBaslangic = null;
        _sorunMailiGitti = false;
        _hatirlatma = 0;
    }

    // Tur kapaliysa ya da alici secilmemisse MailAyarlari gondermez ve sebebini doner --
    // o sebep panele aynen yaziliyor ("uyari neden gelmedi" sorusu ekrandan cevaplansin).
    private async Task<bool> GonderAsync(string tur, Dictionary<string, string> degerler, DateTime simdi, CancellationToken ct)
    {
        var sonuc = await mailAyarlari.GonderAsync(mail, tur, degerler, ct: ct);
        _sonUyari = simdi;
        var ad = MailTurleri.Bul(tur).Ad;
        _sonUyariSonucu = sonuc.Basarili ? $"Gönderildi: {ad}" : $"GÖNDERİLEMEDİ ({ad}): {sonuc.Mesaj}";
        if (sonuc.Basarili) logger.LogWarning("Servis uyarisi gonderildi: {Tur}", tur);
        else logger.LogError("Servis uyarisi gonderilemedi ({Tur}): {Mesaj}", tur, sonuc.Mesaj);
        return sonuc.Basarili;
    }

    private static string SureMetni(TimeSpan s) =>
        s.TotalHours >= 1 ? $"{(int)s.TotalHours} saat {s.Minutes} dk" : $"{(int)s.TotalMinutes} dk";
}
