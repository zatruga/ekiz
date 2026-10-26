using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// ================= YATAN HASTALAR -- ARSIV TAKIBI VE ELLE GONDERIM (2026-10-26) =================
//
// KULLANICI ISTEGI: "yatan hastalarin gonderimlerini manuel yapabilmek ve takip edilmesi
// icin ayri bir ekran yapalim ... bu gonderimi takip edecek departman arsiv, arsive al,
// tamamla fonksiyonlari vs olmali, hatta arsiv numarasi falan vermeli."
//
// KULLANICI KARARLARI: akis taburcu -> Arsive al -> Tamamla; TRƏS'e gonderim AYRI dugme.
// Arsiv numarasi yil bazli sira olarak onerilir, elle degistirilebilir. Ayri "Arsiv" rolu
// yalnizca bu ekrani gorur (bkz. ArsivSayfaKisiti).
//
// KUME: aralikta TABURCU olanlar + su an YATANLAR (bkz. PusulaRepository.GetYatanProtokollerAsync).
// Tarih filtresi taburcu tarihi -- arsivin isi taburcuyla basliyor.
//
// ONBELLEK YOK: liste yuzlerle sinirli (30 gunde ~1.000), her islemden sonra ekranin
// HEMEN guncel durumu gostermesi gerekiyor -- "arsive aldim ama hala bekliyor gorunuyor"
// bu ekranda kabul edilemez.
public class YatanHastalarModel(
    PusulaRepository repository, SyncLogStore syncLog, YatanArsivStore arsiv,
    ProtocolFullSyncService protocolFullSync, SettingsStore settings,
    ILogger<YatanHastalarModel> logger) : PageModel
{
    private const int SayfaBoyutu = 50;
    private const int TopluTavan = 25;

    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    // "yatiyor" | "bekliyor" (taburcu, arsive alinmamis) | "arsivde" | "tamamlandi" | null
    [BindProperty(SupportsGet = true)] public string? Durum { get; set; }
    // "gitmedi" | "eksik" | "hatali" | "gitti" | null -- TRƏS gonderim durumu
    [BindProperty(SupportsGet = true)] public string? Gonderim { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;
    // Gecmisi acilan protokol (satirin altinda hareket gunlugu).
    [BindProperty(SupportsGet = true)] public int? Gecmis { get; set; }

    [TempData] public string? IslemMesaji { get; set; }
    [TempData] public bool IslemBasarili { get; set; }

    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly EffectiveTo { get; private set; }
    public string AktifOrtam { get; private set; } = SettingsStore.EHealthEnvironmentDefault;
    public bool YatanOtomatik { get; private set; }
    public string SiradakiNo { get; private set; } = "";
    public bool PusulaHatasi { get; private set; }
    public bool ArsivKullanicisi => User.IsInRole(UserAccountStore.RoleArsiv);

    public List<Satir> Tumu { get; private set; } = [];
    public List<Satir> Satirlar { get; private set; } = [];
    public int ListelenenSayi { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public bool HasNextPage { get; private set; }
    public List<YatanArsivStore.Hareket> GecmisHareketler { get; private set; } = [];

    public enum GonderimDurumu { Gitmedi, Gitti, Eksik, Hatali }

    public record Satir(
        PusulaRepository.YatanProtokol Yatan,
        YatanArsivStore.ArsivKaydi? Arsiv,
        GonderimDurumu Gonderim, int Basarili, int Eksik, int Hatali,
        DateTime? SonDenemeUtc, List<string> Sebepler)
    {
        public ProtokolListItem P => Yatan.Protokol;
        public string ArsivDurumu => Yatan.Yatiyor ? "yatiyor"
            : Arsiv is null ? "bekliyor"
            : Arsiv.Tamamlandi ? "tamamlandi" : "arsivde";
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var bugun = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? bugun.AddDays(-29);
        EffectiveTo = To ?? bugun;
        PageNumber = P < 1 ? 1 : P;

        AktifOrtam = await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey,
            SettingsStore.EHealthEnvironmentDefault, ct);
        YatanOtomatik = await settings.GetBoolAsync(SettingsStore.AutoSendYatanEnabledKey,
            SettingsStore.AutoSendYatanEnabledDefault, ct);
        SiradakiNo = await arsiv.SiradakiNoAsync(DateTime.Today.Year, ct);

        var tabanHam = await settings.GetStringAsync(SettingsStore.SendFloorDateKey, "", ct);
        DateTime? taban = DateTime.TryParseExact(tabanHam, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? t : null;

        List<PusulaRepository.YatanProtokol> yatanlar;
        try
        {
            yatanlar = await repository.GetYatanProtokollerAsync(
                EffectiveFrom.ToDateTime(TimeOnly.MinValue), EffectiveTo.AddDays(1).ToDateTime(TimeOnly.MinValue),
                taban, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Yatan Hastalar: liste Pusula'dan okunamadi.");
            PusulaHatasi = true;
            return;
        }

        var ids = yatanlar.Select(y => y.Protokol.ProtokolId).ToList();
        var arsivler = await arsiv.GetByProtokolIdsAsync(ids, ct);

        // GONDERIM DURUMU -- aktif ortamda, her kaydin SON denemesine gore (Aktivite ile
        // ayni kural). Hasta (Patient) kaydi bilerek disarida: hasta protokoller arasi
        // paylasiliyor, baska bir protokolun hastasi bu satiri boyamasin.
        var ozet = await syncLog.QueryOzetByProtokolIdsAsync(ids, [], null, AktifOrtam, ct);
        var protokolBasina = ozet.Where(o => o.ProtokolId is not null).ToLookup(o => o.ProtokolId!.Value);
        var zamanlar = await syncLog.GetZamanlarAsync(
            ids.Select(id => protokolBasina[id].Select(o => o.SonId).DefaultIfEmpty(0).Max()).Where(i => i > 0).ToList(), ct);

        Tumu = yatanlar.Select(y =>
        {
            var kayitlar = protokolBasina[y.Protokol.ProtokolId].ToList();
            int b = kayitlar.Count(k => k.Status == SyncStatus.Success),
                e = kayitlar.Count(k => k.Status == SyncStatus.Skipped),
                h = kayitlar.Count(k => k.Status == SyncStatus.Failed);
            var durum = kayitlar.Count == 0 ? GonderimDurumu.Gitmedi
                : h > 0 ? GonderimDurumu.Hatali
                : e > 0 ? GonderimDurumu.Eksik
                : GonderimDurumu.Gitti;
            var sonId = kayitlar.Count == 0 ? 0 : kayitlar.Max(k => k.SonId);
            // Sebep: hata ve eksiklerin kategorisi, en sik once -- arsivci "neden gitmedi"
            // sorusunu satirda gorsun.
            var sebepler = kayitlar.Where(k => k.Status != SyncStatus.Success && k.HataMesaji is not null)
                .GroupBy(k => SyncLogEntry.ErrorCategory(k.HataMesaji).Label)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Count() > 1 ? $"{g.Key} ({g.Count()})" : g.Key)
                .Take(3).ToList();
            return new Satir(y, arsivler.GetValueOrDefault(y.Protokol.ProtokolId), durum, b, e, h,
                zamanlar.TryGetValue(sonId, out var z) ? z : null, sebepler);
        }).ToList();

        var aranan = Q?.Trim();
        var liste = Tumu
            .Where(s => Durum is null || s.ArsivDurumu == Durum)
            .Where(s => Gonderim is null || GonderimAnahtari(s.Gonderim) == Gonderim)
            .Where(s => string.IsNullOrEmpty(aranan) || AramayaUyar(s, aranan))
            .ToList();
        ListelenenSayi = liste.Count;
        Satirlar = liste.Skip((PageNumber - 1) * SayfaBoyutu).Take(SayfaBoyutu).ToList();
        HasNextPage = liste.Count > PageNumber * SayfaBoyutu;

        if (Gecmis is { } g) GecmisHareketler = await arsiv.HareketlerAsync(g, ct);
    }

    public static string GonderimAnahtari(GonderimDurumu d) => d switch
    {
        GonderimDurumu.Gitti => "gitti",
        GonderimDurumu.Eksik => "eksik",
        GonderimDurumu.Hatali => "hatali",
        _ => "gitmedi",
    };

    private static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    private static bool AramayaUyar(Satir s, string aranan)
    {
        var a = aranan.ToUpper(Tr);
        return s.P.ProtokolId.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(a)
            || (s.P.Fin?.ToUpper(Tr).Contains(a) ?? false)
            || ($"{s.P.HastaAdi} {s.P.HastaSoyadi}".ToUpper(Tr).Contains(a))
            || (s.Arsiv?.ArsivNo.ToUpper(Tr).Contains(a) ?? false);
    }

    // ---------------------------------------------------------------- ARSIV ISLEMLERI

    private string Kullanici => User.FindFirstValue(ClaimTypes.Name) ?? "?";

    // Bos numara = siradaki numara otomatik. Iki kisi ayni anda ayni bos numarayi alirsa
    // UNIQUE kurali ikinciyi reddeder; o yuzden otomatikte birkac kez yeniden deneniyor.
    private async Task<(string? Hata, string No)> ArsiveAlAsync(int protokolId, string? arsivNo, string? notu, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(arsivNo))
            return (await arsiv.ArsiveAlAsync(protokolId, arsivNo, Kullanici, notu, ct), arsivNo.Trim());

        string? hata = null;
        var no = "";
        for (var deneme = 0; deneme < 5; deneme++)
        {
            no = await arsiv.SiradakiNoAsync(DateTime.Today.Year, ct);
            hata = await arsiv.ArsiveAlAsync(protokolId, no, Kullanici, notu, ct);
            if (hata is null || hata.StartsWith("Bu protokol", StringComparison.Ordinal)) break;
        }
        return (hata, no);
    }

    public async Task<IActionResult> OnPostArsiveAlAsync(int protokolId, string? arsivNo, string? notu, CancellationToken ct)
    {
        var (hata, no) = await ArsiveAlAsync(protokolId, arsivNo, notu, ct);
        Sonuc(hata is null, hata ?? $"Protokol {protokolId} arşive alındı · arşiv no {no}.");
        return GeriDon();
    }

    public async Task<IActionResult> OnPostTopluArsiveAlAsync(List<int> protokolIds, CancellationToken ct)
    {
        var hedefler = protokolIds.Distinct().Take(TopluTavan).ToList();
        if (hedefler.Count == 0) { Sonuc(false, "Protokol seçilmedi."); return GeriDon(); }
        var alinan = new List<string>();
        var hatalar = new List<string>();
        foreach (var pid in hedefler)
        {
            var (hata, no) = await ArsiveAlAsync(pid, null, null, ct);
            if (hata is null) alinan.Add(no); else hatalar.Add($"{pid}: {hata}");
        }
        Sonuc(hatalar.Count == 0,
            $"{alinan.Count} dosya arşive alındı"
            + (alinan.Count > 0 ? $" ({alinan.First()} – {alinan.Last()})" : "")
            + (hatalar.Count > 0 ? $". Alınamayan: {string.Join("; ", hatalar)}" : "."));
        return GeriDon();
    }

    public async Task<IActionResult> OnPostNoDegistirAsync(int protokolId, string arsivNo, CancellationToken ct)
    {
        var hata = await arsiv.NoDegistirAsync(protokolId, arsivNo ?? "", Kullanici, ct);
        Sonuc(hata is null, hata ?? $"Protokol {protokolId} arşiv numarası {arsivNo?.Trim()} olarak değiştirildi.");
        return GeriDon();
    }

    public async Task<IActionResult> OnPostTamamlaAsync(int protokolId, string? notu, CancellationToken ct)
    {
        var hata = await arsiv.TamamlaAsync(protokolId, Kullanici, notu, ct);
        Sonuc(hata is null, hata ?? $"Protokol {protokolId} arşivde tamamlandı.");
        return GeriDon();
    }

    public async Task<IActionResult> OnPostGeriAlAsync(int protokolId, CancellationToken ct)
    {
        var hata = await arsiv.TamamlamayiGeriAlAsync(protokolId, Kullanici, ct);
        Sonuc(hata is null, hata ?? $"Protokol {protokolId} için tamamlama geri alındı.");
        return GeriDon();
    }

    public async Task<IActionResult> OnPostArsivdenCikarAsync(int protokolId, CancellationToken ct)
    {
        var hata = await arsiv.ArsivdenCikarAsync(protokolId, Kullanici, ct);
        Sonuc(hata is null, hata ?? $"Protokol {protokolId} arşivden çıkarıldı.");
        return GeriDon();
    }

    // ---------------------------------------------------------------- TRƏS GONDERIMI

    // Otomatik dongunun yaptiginin AYNISI (ProtocolFullSyncService.SyncAllAsync) -- elle
    // gonderim ayri bir kod yolu degil, yalnizca tetikleyen farkli. Gonderim, aktif ortama
    // gider (Ayarlar'daki ortam); ekran bunu basligin yaninda yaziyor.
    public async Task<IActionResult> OnPostGonderAsync(int protokolId, CancellationToken ct)
    {
        var (basarili, mesaj) = await GonderAsync(protokolId, ct);
        Sonuc(basarili, mesaj);
        return GeriDon();
    }

    public async Task<IActionResult> OnPostTopluGonderAsync(List<int> protokolIds, CancellationToken ct)
    {
        var hedefler = protokolIds.Distinct().Take(TopluTavan).ToList();
        if (hedefler.Count == 0) { Sonuc(false, "Protokol seçilmedi."); return GeriDon(); }
        int iyi = 0, kotu = 0;
        foreach (var pid in hedefler)
        {
            var (basarili, _) = await GonderAsync(pid, ct);
            if (basarili) iyi++; else kotu++;
        }
        Sonuc(kotu == 0, $"{hedefler.Count} protokol gönderildi: {iyi} başarılı, {kotu} başarısız. Ayrıntı satırlarda.");
        return GeriDon();
    }

    private async Task<(bool Basarili, string Mesaj)> GonderAsync(int protokolId, CancellationToken ct)
    {
        var protokol = await repository.GetProtokolByIdAsync(protokolId, ct);
        if (protokol is null) return (false, $"Protokol {protokolId} Pusula'da bulunamadı.");
        try
        {
            var sonuc = await protocolFullSync.SyncAllAsync(protokol, ct);
            var basarili = sonuc.EncounterStatus != SyncStatus.Failed;
            await arsiv.GonderimHareketiYazAsync(protokolId, Kullanici,
                $"Muayene: {sonuc.EncounterStatus} · ortam {AktifOrtamAdi(await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct))}", ct);
            return (basarili, $"Protokol {protokolId} TRƏS'e gönderildi (muayene: {sonuc.EncounterStatus}).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Yatan Hastalar: protokol {Id} gonderilemedi.", protokolId);
            return (false, $"Protokol {protokolId} gönderilemedi: {ex.Message}");
        }
    }

    public static string AktifOrtamAdi(string ortam) => ortam == "Live" ? "Canlı" : "Sandbox (Test)";

    private void Sonuc(bool basarili, string mesaj)
    {
        IslemBasarili = basarili;
        IslemMesaji = mesaj;
    }

    // POST-REDIRECT-GET: filtre ve sayfa korunuyor (bkz. AktiviteModel.GeriDon).
    private IActionResult GeriDon() => RedirectToPage("/YatanHastalar", new
    {
        From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd"),
        Durum, Gonderim, Q, P = P < 1 ? 1 : P, Gecmis,
    });
}
