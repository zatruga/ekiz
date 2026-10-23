using System.Text.Json;
using System.Text.Json.Serialization;

namespace PusulaEHealthSync.Persistence;

// GONDERIM SERVISININ DURUMU -- bekcinin (PusulaSyncUpdater) yazdigi, panelin okudugu dosya.
// (2026-10-23, kullanici istegi: "servisi yeniden baslatma ve servis durumunu gosteren bir
// panel olmali".)
//
// NEDEN DOSYA: Web IIS havuzu hesabiyla calisiyor ve Windows servislerini sorgulayip
// yeniden baslatma yetkisi yok (olmamali da). Guncelleyici zaten servisleri yoneten,
// surekli calisan bir Windows servisi ve Web ile kontrol klasoru uzerinden konusuyor
// (update.trigger / update-status.json). Ayni kanal burada da kullaniliyor.
//
// Bu sinif Web ile guncelleyici arasindaki SOZLESME: ikisi de bu tipi kullaniyor, ayri
// kopya tutulmuyor (bkz. ServisKayitlari'ndaki "iki liste ayristi" dersi).
public record ServisDurumu
{
    public const string DosyaAdi = "servis-durum.json";
    public const string YenidenBaslatTetikleyici = "servis-yeniden-baslat.trigger";

    // Bekcinin son kontrol ani. Bayatsa bekcinin KENDISI calismiyor (ya da eski surum).
    public DateTime KontrolUtc { get; init; }
    public string ServisAdi { get; init; } = "";

    // Windows'un soyledigi: Running / Stopped / StartPending ... ya da "Bulunamadi".
    public string WindowsDurumu { get; init; } = "";
    public string BaslangicTuru { get; init; } = "";   // Automatic / Manual / Disabled

    public DateTime? NabizUtc { get; init; }
    public bool OtomatikGonderimAcik { get; init; }
    public bool GuncellemeSuruyor { get; init; }

    public bool Saglikli { get; init; }
    public string? Sorun { get; init; }
    public DateTime? SorunBaslangicUtc { get; init; }

    // Bu sorun icin "durdu" maili gitti mi? Bekci yeniden baslarsa ayni sorun icin ikinci
    // kez "durdu" maili atmasin, duzelince de "duzeldi" mailini atmayi unutmasin.
    public bool SorunMailiGitti { get; init; }
    public DateTime? SonUyariUtc { get; init; }
    public string? SonUyariSonucu { get; init; }

    public YenidenBaslatma? SonYenidenBaslatma { get; init; }

    public record YenidenBaslatma(DateTime Utc, string? Isteyen, bool Basarili, string Mesaj);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ServisDurumu? Oku(string kontrolKlasoru)
    {
        var yol = Path.Combine(kontrolKlasoru, DosyaAdi);
        if (!File.Exists(yol)) return null;
        try { return JsonSerializer.Deserialize<ServisDurumu>(File.ReadAllText(yol), Json); }
        catch { return null; }   // yazilirken okunduysa bir sonraki okumada duzelir
    }

    // Once gecici dosyaya yazip yerine tasiyor: Web yarim yazilmis bir dosyayi okumasin.
    public void Yaz(string kontrolKlasoru)
    {
        var yol = Path.Combine(kontrolKlasoru, DosyaAdi);
        var gecici = yol + ".tmp";
        File.WriteAllText(gecici, JsonSerializer.Serialize(this, Json));
        File.Move(gecici, yol, overwrite: true);
    }

    // NABIZ BAYAT MI -- Aktivite seridi ve bekci AYNI kurali kullaniyor. Esik: tur
    // araliginin 3 kati, en az 30 dk (tek bir agir adim -- bekleyen is taramasi, iptal
    // senkronu -- birkac dakika surebiliyor, yanlis alarm olmasin). Nabiz hic yoksa bayat.
    public static bool NabizBayat(DateTime? nabizUtc, int aralikDakika, DateTime simdiUtc) =>
        nabizUtc is not { } n || simdiUtc - n > TimeSpan.FromMinutes(Math.Max(30, 3 * aralikDakika));
}
