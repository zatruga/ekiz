using System.Net;
using System.Net.Mail;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Reporting;

// E-POSTA GONDERICI (2026-10-05).
//
// Ayarlar'daki Mail.* alanlari 2026-08'den beri duruyordu -- kaydediliyor, geri okunuyor,
// ama HICBIR KOD mail gondermiyordu. Bu projede ayni tuzagin UCUNCU ornegi (once otomatik
// gonderim anahtarlari, sonra Retry.* alanlari). Ekranda calisiyor gorunen bir ayarin
// arkasinda hicbir sey olmamasi, en pahali hata turu: kimse sikayet etmiyor cunku herkes
// calistigini saniyor.
//
// NEDEN MAILKIT DEGIL: System.Net.Mail BCL'de, yeni bir NuGet bagimliligi gerektirmiyor.
// MailKit daha modern ama buradaki ihtiyac ic aga kurulu bir Exchange relay'ine
// (mail.mlpcare.com:25) duz bir HTML mail atmak. Hastane sunucusuna paket eklemek --
// guncelleme, imza, offline kurulum -- kazancindan buyuk bir bedel.
public class MailSender(SettingsStore settings, ILogger<MailSender> logger)
{
    // Ag islemi worker'i KILITLEMEMELI: SMTP sunucusu yanit vermezse tur burada beklemesin.
    private static readonly TimeSpan ZamanAsimi = TimeSpan.FromSeconds(30);

    public record Sonuc(bool Basarili, string Mesaj, int AliciSayisi)
    {
        public static Sonuc Hata(string mesaj) => new(false, mesaj, 0);
    }

    // Alici listesini ayristirir. Virgul, noktali virgul, yeni satir ve bosluk kabul eder --
    // kullanici kutuya nasil yazarsa yazsin calismali.
    public static List<string> AlicilariAyristir(string? ham) => (ham ?? "")
        .Split([',', ';', '\n', '\r', ' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(a => a.Contains('@'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public async Task<Sonuc> GonderAsync(string konu, string htmlGovde, CancellationToken ct = default)
    {
        var acik = await settings.GetBoolAsync(SettingsStore.MailEnabledKey, false, ct);
        if (!acik) return Sonuc.Hata("E-posta gönderimi Ayarlar'da kapalı.");

        var alicilar = AlicilariAyristir(
            await settings.GetStringAsync(SettingsStore.MailRecipientsKey, "", ct));
        if (alicilar.Count == 0)
            return Sonuc.Hata("Alıcı tanımlanmamış. Ayarlar → Gün Sonu Maili bölümüne en az bir adres girin.");

        return await GonderAsync(konu, htmlGovde, alicilar, ct);
    }

    // Alicilari acikca alan asiri yukleme -- Ayarlar'daki "Test maili gonder" dugmesi
    // bunu kullaniyor (henuz kaydedilmemis adrese test atilabilsin diye).
    public async Task<Sonuc> GonderAsync(
        string konu, string htmlGovde, IReadOnlyCollection<string> alicilar, CancellationToken ct = default)
    {
        if (alicilar.Count == 0) return Sonuc.Hata("Alıcı listesi boş.");

        var host = (await settings.GetStringAsync(SettingsStore.MailSmtpHostKey, SettingsStore.MailSmtpHostDefault, ct)).Trim();
        if (host.Length == 0) return Sonuc.Hata("SMTP sunucu adresi girilmemiş.");

        var port = await settings.GetIntAsync(SettingsStore.MailSmtpPortKey, SettingsStore.MailSmtpPortDefault, ct);
        var tls = await settings.GetBoolAsync(SettingsStore.MailUseTlsKey, false, ct);
        var kullanici = (await settings.GetStringAsync(SettingsStore.MailUsernameKey, "", ct)).Trim();
        var sifre = await settings.GetStringAsync(SettingsStore.MailPasswordKey, "", ct);
        var gonderen = (await settings.GetStringAsync(SettingsStore.MailFromAddressKey, SettingsStore.MailFromAddressDefault, ct)).Trim();
        if (gonderen.Length == 0) gonderen = SettingsStore.MailFromAddressDefault;

        try
        {
            using var mesaj = new MailMessage
            {
                From = new MailAddress(gonderen, "Pusula TRƏS Entegrasyonu"),
                Subject = konu,
                Body = htmlGovde,
                IsBodyHtml = true,
            };
            foreach (var a in alicilar) mesaj.To.Add(a);

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = tls,
                Timeout = (int)ZamanAsimi.TotalMilliseconds,
                DeliveryMethod = SmtpDeliveryMethod.Network,
            };

            // KIMLIK DOGRULAMA OPSIYONEL: ic agdaki Exchange relay'leri genelde kimlik
            // istemez ve bos kullanici adiyla Credentials vermek bazi sunucularda
            // "535 authentication failed" ile reddedilmeye yol aciyor. Bu yuzden kullanici
            // adi gercekten girilmisse gonderiliyor.
            if (kullanici.Length > 0)
                client.Credentials = new NetworkCredential(kullanici, sifre);
            else
                client.UseDefaultCredentials = false;

            await client.SendMailAsync(mesaj, ct);

            logger.LogInformation("E-posta gonderildi: {Konu} -> {Adet} alici.", konu, alicilar.Count);
            return new Sonuc(true, $"{alicilar.Count} alıcıya gönderildi.", alicilar.Count);
        }
        catch (Exception ex)
        {
            // MESAJ KULLANICIYA GOSTERILIYOR, bu yuzden ham istisna yerine okunabilir bir
            // ozet. Ayrintiyi log tasiyor.
            logger.LogError(ex, "E-posta gonderilemedi ({Host}:{Port}).", host, port);
            return Sonuc.Hata(AnlasilirHata(ex, host, port));
        }
    }

    // SMTP hatalari ham halde ("Failure sending mail.") hicbir sey anlatmiyor; en sik
    // gorulen uc durumun karsiligi yaziliyor -- ayni ilke SyncLogEntry.FriendlyError'da.
    private static string AnlasilirHata(Exception ex, string host, int port)
    {
        var m = ex.ToString();

        if (m.Contains("No such host", StringComparison.OrdinalIgnoreCase)
            || m.Contains("known", StringComparison.OrdinalIgnoreCase))
            return $"SMTP sunucusu bulunamadı ({host}). Sunucu adresini kontrol edin.";

        if (m.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || m.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || m.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || m.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return $"SMTP sunucusuna bağlanılamadı ({host}:{port}). Port kapalı olabilir ya da "
                 + "güvenlik duvarı engelliyor olabilir.";

        if (m.Contains("5.7.", StringComparison.OrdinalIgnoreCase)
            || m.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not authenticated", StringComparison.OrdinalIgnoreCase))
            return "SMTP kimlik doğrulaması başarısız. Kullanıcı adı/şifreyi kontrol edin; "
                 + "iç ağdaki sunucular genelde kimlik istemez, bu alanları boş bırakmayı deneyin.";

        if (m.Contains("5.7.1", StringComparison.OrdinalIgnoreCase)
            || m.Contains("relay", StringComparison.OrdinalIgnoreCase))
            return "SMTP sunucusu bu adresten gönderime izin vermiyor (relay reddi). "
                 + "Gönderen adresin sunucuda yetkili olması gerekiyor.";

        return $"E-posta gönderilemedi: {ex.Message}";
    }
}
