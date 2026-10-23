using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Reporting;

// E-POSTA TURLERI (2026-10-23).
//
// KULLANICI ISTEGI: "mail gonderim ayarlarinda servis bilgilendirme, durum vs her bilgi
// maili icin tanimlama yapilacak bir alan olmali; mail icerigi ve mail gonderilecek kisileri
// secim yapmam gerekli" + "zamanlama ayarlari vs de olmali".
//
// ONCE tek bir alici listesi ve tek bir ac/kapa vardi, ikisi de gun sonu raporuna bagliydi.
// Ikinci bir e-posta (servis uyarisi) eklenince bu yetmedi: rapor kapaliyken uyari gitmeli,
// uyariyi baska kisiler almak isteyebilir.
//
// YAPI: SMTP ortak, KISILER bir kez tanimlanir, her TUR kendi ac/kapa'sini, alicilarini
// (kisilerden secilen), konusunu, mesajini ve zamanlamasini tasir. Zamanlama alanlari
// SettingsStore'daki mevcut anahtarlar -- yalnizca ekranda ilgili turun yanina tasindi.
public static class MailTurleri
{
    public const string GunSonu = "GunSonu";
    public const string ServisDurdu = "ServisDurdu";
    public const string ServisDuzeldi = "ServisDuzeldi";

    public record Tanim(
        string Kod, string Ad, string Aciklama,
        string VarsayilanKonu, string VarsayilanMesaj,
        IReadOnlyList<(string Ad, string Aciklama, string Ornek)> Degiskenler,
        bool VarsayilanAcik);

    public static readonly IReadOnlyList<Tanim> Hepsi =
    [
        new(GunSonu, "Gün sonu raporu",
            "Her sabah belirlenen saatte bir önceki günün gönderim bilançosu: kaç protokol denendi, kaçı " +
            "başarılı, kaçı hatalı ve gönderilemeyen kayıtların sebepleri. Sorun olmayan günlerde de gelir: " +
            "mail gelmediği gün sistemin çalışmadığı anlaşılır.",
            "{otomatik_konu}",
            "",
            [
                ("otomatik_konu", "Duruma göre kurulan konu (ör. \"… 12 protokolde hata\")",
                    "[CANLI] TRƏS gun sonu 22.10.2026 -- 12 protokolde hata"),
                ("tarih", "Raporlanan gün", "22.10.2026"),
                ("ortam", "Test / CANLI", "CANLI"),
                ("denenen", "Denenen protokol", "3.613"),
                ("basarili", "Başarılı protokol", "3.380"),
                ("hatali", "Hatalı protokol", "12"),
                ("eksik", "Eksik verili protokol", "221"),
            ],
            VarsayilanAcik: false),

        new(ServisDurdu, "Gönderim durdu uyarısı",
            "Otomatik gönderim servisi çalışmıyorsa ya da takılmışsa gönderilir. Panel açık olmasa da gider: " +
            "uyarıyı güncelleyici servisi atar. Sorun sürdükçe belirlenen aralıkla hatırlatılır.",
            "[Pusula → TRƏS] Otomatik gönderim DURDU",
            "Otomatik gönderim {baslangic} saatinden beri çalışmıyor ({sure}).\n\n" +
            "Sebep: {sorun}\n\n" +
            "Bu süre boyunca bakanlık sistemine kayıt gönderilmiyor. Panelde Servis Durumu sayfasından " +
            "servisi yeniden başlatın. Panel açılmıyorsa sunucuda services.msc içinden {servis} servisini başlatın.",
            [
                ("sorun", "Ne olduğu", "Servis çalışmıyor (Windows durumu: Stopped)."),
                ("baslangic", "Sorunun başladığı an", "23.10.2026 09:40"),
                ("sure", "Ne kadardır sürüyor", "1 saat 25 dk"),
                ("servis", "Windows servis adı", "PusulaSyncWorker"),
                ("hatirlatma", "Kaçıncı hatırlatma (ilk uyarıda 0)", "0"),
            ],
            VarsayilanAcik: true),

        new(ServisDuzeldi, "Gönderim yeniden çalışıyor bildirimi",
            "\"Durdu\" uyarısı gönderilmiş bir sorun düzelince gönderilir. Uyarı hiç gitmeden kısa sürede " +
            "düzelen kesintiler için gönderilmez.",
            "[Pusula → TRƏS] Otomatik gönderim yeniden çalışıyor",
            "Otomatik gönderim {baslangic} ile {bitis} arasında ({sure}) çalışmadı ve şimdi yeniden çalışıyor.\n\n" +
            "Bekleyen kayıtlar sıradaki turlarda kendiliğinden gönderilir; ayrıca bir şey yapmanız gerekmiyor.",
            [
                ("baslangic", "Kesintinin başladığı an", "23.10.2026 09:40"),
                ("bitis", "Düzeldiği an", "23.10.2026 11:05"),
                ("sure", "Kesinti süresi", "1 saat 25 dk"),
            ],
            VarsayilanAcik: true),
    ];

    public static Tanim Bul(string kod) =>
        Hepsi.FirstOrDefault(t => t.Kod == kod) ?? throw new ArgumentException($"Bilinmeyen e-posta türü: {kod}");

    public static Dictionary<string, string> OrnekDegerler(string kod) =>
        Bul(kod).Degiskenler.ToDictionary(d => d.Ad, d => d.Ornek);

    // {degisken} -> deger. Bilinmeyen degisken OLDUGU GIBI kalir: yazim hatasi sessizce
    // bos metne donusmesin, okuyan gorsun.
    public static string Doldur(string sablon, IReadOnlyDictionary<string, string> degerler) =>
        Regex.Replace(sablon ?? "", @"\{([a-z_]+)\}",
            m => degerler.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    // Kullanicinin yazdigi duz metin -> guvenli HTML. HTML yazmasina izin verilmiyor:
    // ayar ekranina giren herkes alicilarin gelen kutusuna betik gondermesin.
    public static string MesajHtml(string mesaj) =>
        string.IsNullOrWhiteSpace(mesaj) ? "" :
        string.Join("", mesaj.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"<p style=\"margin:0 0 12px\">{WebUtility.HtmlEncode(p.Trim()).Replace("\n", "<br>")}</p>"));
}

// KISILER + TUR AYARLARI -- SettingsStore uzerinde ince bir katman.
public class MailAyarlari(SettingsStore settings)
{
    public record Kisi(string Ad, string Eposta);
    public record TurAyari(bool Acik, List<string> Alicilar, string Konu, string Mesaj);

    private const string KisilerKey = "Mail.Kisiler";
    private static string Anahtar(string kod, string alan) => $"Mail.Tur.{kod}.{alan}";

    public async Task<List<Kisi>> KisileriOkuAsync(CancellationToken ct = default)
    {
        var ham = await settings.GetStringAsync(KisilerKey, "", ct);
        if (ham.Length > 0)
        {
            try { return JsonSerializer.Deserialize<List<Kisi>>(ham) ?? []; }
            catch (JsonException) { /* bozuksa eski listeden yeniden tohumla */ }
        }

        // GECIS: kisi listesi hic yoksa eski "Alicilar" alanindaki adresler kisi olur.
        // Kullanicinin daha once girdigi hicbir adres kaybolmasin.
        return MailSender.AlicilariAyristir(await settings.GetStringAsync(SettingsStore.MailRecipientsKey, "", ct))
            .Select(a => new Kisi("", a)).ToList();
    }

    public Task KisileriYazAsync(IEnumerable<Kisi> kisiler, CancellationToken ct = default) =>
        settings.SetStringAsync(KisilerKey, JsonSerializer.Serialize(kisiler.ToList()), ct);

    public async Task<TurAyari> OkuAsync(string kod, CancellationToken ct = default)
    {
        var tanim = MailTurleri.Bul(kod);

        // GECIS (gun sonu): eski Mail.Enabled ve Mail.Recipients, turun kendi anahtari
        // yazilana kadar gecerli -- guncellemeden sonra rapor kendiliginden kesilmesin.
        var acikVarsayilan = kod == MailTurleri.GunSonu
            ? await settings.GetBoolAsync(SettingsStore.MailEnabledKey, false, ct)
            : tanim.VarsayilanAcik;
        var eskiAlicilar = await settings.GetStringAsync(SettingsStore.MailRecipientsKey, "", ct);

        return new TurAyari(
            await settings.GetBoolAsync(Anahtar(kod, "Acik"), acikVarsayilan, ct),
            MailSender.AlicilariAyristir(await settings.GetStringAsync(Anahtar(kod, "Alicilar"), eskiAlicilar, ct)),
            await settings.GetStringAsync(Anahtar(kod, "Konu"), tanim.VarsayilanKonu, ct),
            await settings.GetStringAsync(Anahtar(kod, "Mesaj"), tanim.VarsayilanMesaj, ct));
    }

    public async Task YazAsync(string kod, TurAyari ayar, CancellationToken ct = default)
    {
        MailTurleri.Bul(kod);
        await settings.SetBoolAsync(Anahtar(kod, "Acik"), ayar.Acik, ct);
        await settings.SetStringAsync(Anahtar(kod, "Alicilar"), string.Join(", ", ayar.Alicilar), ct);
        await settings.SetStringAsync(Anahtar(kod, "Konu"), ayar.Konu.Trim(), ct);
        await settings.SetStringAsync(Anahtar(kod, "Mesaj"), ayar.Mesaj.Trim(), ct);
        // Gun sonu raporunu okuyan eski kod yollari icin ayna (AutoSyncWorker'in "mail
        // kapaliysa raporu hesaplama bile" kisayolu).
        if (kod == MailTurleri.GunSonu)
            await settings.SetBoolAsync(SettingsStore.MailEnabledKey, ayar.Acik, ct);
    }

    // TURU GONDER: tur kapaliysa ya da alici yoksa GONDERMEZ ve sebebini soyler.
    // ekHtml: mesajin altina eklenen, kodun urettigi icerik (gun sonu raporunun tablosu).
    public async Task<MailSender.Sonuc> GonderAsync(
        MailSender mail, string kod, IReadOnlyDictionary<string, string> degerler,
        string? ekHtml = null, bool test = false, CancellationToken ct = default)
    {
        var tanim = MailTurleri.Bul(kod);
        var ayar = await OkuAsync(kod, ct);
        if (!ayar.Acik && !test) return MailSender.Sonuc.Hata($"\"{tanim.Ad}\" e-postası Ayarlar'da kapalı.");
        if (ayar.Alicilar.Count == 0)
            return MailSender.Sonuc.Hata($"\"{tanim.Ad}\" için alıcı seçilmemiş (Ayarlar → Mail).");

        var konu = MailTurleri.Doldur(ayar.Konu, degerler);
        if (test) konu = "[TEST] " + konu;
        return await mail.GonderAsync(konu, Govde(ayar, degerler, ekHtml, test), ayar.Alicilar, ct);
    }

    // On izleme de AYNI govdeyi kullaniyor -- ekranda gorulen ile giden ayni olsun.
    public static string Govde(TurAyari ayar, IReadOnlyDictionary<string, string> degerler, string? ekHtml, bool test)
    {
        var ust =
            "<div style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#15181D;max-width:720px;margin:0 auto 12px\">"
            + (test ? "<p style=\"padding:8px 12px;background:#FBF7EF;border:1px solid #EAD9B8;border-radius:6px\">" +
                      "Bu bir <strong>test</strong> e-postasıdır.</p>" : "")
            + MailTurleri.MesajHtml(MailTurleri.Doldur(ayar.Mesaj, degerler))
            + "</div>";

        // Ek tam bir HTML belgesiyse (gun sonu raporu) mesaj <body>'nin hemen icine konur;
        // bir div'in icine belge gommek e-posta istemcilerinde dagiliyor.
        if (ekHtml is not null)
        {
            var body = ekHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            var kapanis = body < 0 ? -1 : ekHtml.IndexOf('>', body);
            if (kapanis > 0) return ekHtml.Insert(kapanis + 1, ust);
        }
        return "<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\"></head><body>"
            + ust + (ekHtml ?? "") + "</body></html>";
    }
}
