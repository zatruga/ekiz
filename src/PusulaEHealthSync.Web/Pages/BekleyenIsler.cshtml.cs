using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// "Bekleyen Isler" -- Pusula'da hazir olup TRƏS'e gitmemis her sey.
//
// SALT-OKUNUR: bu sayfa HICBIR SEY GONDERMEZ. Amaci, otomatik gonderimi acmadan once
// prod'da neyin biriktigini gorunur kilmak. Gonderim isteniyorsa protokol satirindan
// Protokol Detay'a gidilip oradaki "Tumunu Gonder" kullanilir, ya da Ayarlar'dan otomatik
// gonderim acilir (bkz. AutoSyncWorker).
//
// ONBELLEKTEN OKUR: tam tarama canli veride ~15 sn suruyor, her acilista calistirilamaz.
// Sonuc PendingWorkService'te tutulur; saatlik dongu her turunda tazeler, kullanici da
// "Yenile" ile zorlayabilir.
[Authorize]
public class BekleyenIslerModel(PendingWorkService pendingWork, SettingsStore settings) : PageModel
{
    public PendingWorkResult? Sonuc { get; private set; }
    public DateTime? SonHesaplamaUtc { get; private set; }
    public bool OtomatikGonderimAcik { get; private set; }

    // KULLANICI ISTEGI (2026-10-03): "otomatik gönderimde sistem sandbox seçili ise sandbox
    // gönderim yapsın, ya da canlı seçili ise canlı". Davranis zaten boyleydi (EHealthClient
    // ortami her istekte ayarlardan okuyor) ama EKRANDA hicbir yerde yazmiyordu -- otomatik
    // gonderimi acan kisinin nereye yazdigini tahmin etmesi gerekiyordu.
    public string Ortam { get; private set; } = "Test";

    // "Neden bekliyor" aciklamasi icin (KULLANICI ISTEGI 2026-10-08). Uygun bir protokolun
    // neden hala gitmedigi sorusunun cevabi sira ve hiz: her turda en fazla kac protokol
    // gonderiliyor ve tur kac dakikada bir donuyor.
    public int PartiBoyutu { get; private set; }
    public int TurAraligiDakika { get; private set; }

    // TARIH ARALIGI (KULLANICI ISTEGI 2026-09-29): "bekleyen islere 2 tarih secimi
    // koyalim, baslangic tarihi ve bitme tarihi, bu iki tarih arasini hesaplasin."
    //
    // Bos birakilirsa Ayarlar > Tarama Araligi'ndaki pencere kullanilir; boylece sayfa
    // ilk acildiginda ayarla ayni sonucu gosterir. Bitis GUN SONUNA kadar dahildir
    // (sorguya bir sonraki gunun 00:00'i gonderiliyor) -- kullanici 29.09 sectiginde
    // 29.09'da sonuclanmis kayitlar da listeye girmeli.
    [BindProperty(SupportsGet = true)]
    [DataType(DataType.Date)]
    public DateOnly? Baslangic { get; set; }

    [BindProperty(SupportsGet = true)]
    [DataType(DataType.Date)]
    public DateOnly? Bitis { get; set; }

    // Ekranda gosterilen fiili aralik.
    public DateOnly EtkinBaslangic { get; private set; }
    public DateOnly EtkinBitis { get; private set; }
    public string? UyariMesaji { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        OtomatikGonderimAcik = await settings.GetBoolAsync(SettingsStore.AutoSendEncounterEnabledKey, false, ct);
        Ortam = await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);
        PartiBoyutu = await settings.GetIntAsync(
            SettingsStore.AutoSendBatchSizeKey, SettingsStore.AutoSendBatchSizeDefault, ct);
        TurAraligiDakika = await settings.GetIntAsync(
            SettingsStore.AutoSendIntervalMinutesKey, SettingsStore.AutoSendIntervalMinutesDefault, ct);
        await AralikHesaplaAsync(ct);
        Sonuc = pendingWork.SonSonuc;
        SonHesaplamaUtc = pendingWork.SonHesaplamaUtc;
        if (Sonuc is not null) Dokum = BekleyenDokum.Hesapla(Sonuc);
    }

    public TarihSecici TarihSecici => new(EtkinBaslangic, EtkinBitis, "Sonuçlanma",
        "Pusula'da bu aralıkta sonuçlanan kalemler taranır");

    // Asama/sebep dokumu (bkz. BekleyenDokum). Sonuc yoksa null.
    public BekleyenDokum? Dokum { get; private set; }

    // Eski ayrintili tablolar artik sayfanin altinda, katlanmis; tarama kirpilmadigi icin
    // binlerce satir olabilir -- ekrana bu kadari basiliyor.
    public const int AyrintiSatirSiniri = 200;

    // "LISTELE" PENCERESI -- bir sebep satirinin protokolleri (fetch ile <dialog>'a).
    public IActionResult OnGetListe(string anahtar)
    {
        var s = pendingWork.SonSonuc;
        if (s is null) return Content("Önce hesaplayın.");
        var grup = BekleyenDokum.Hesapla(s).Grup(anahtar);
        return grup is null ? Content("Bu sebepte protokol kalmadı — sayfayı yenileyin.") : Partial("_BekleyenListe", grup);
    }

    // "BU PROTOKOL NEDEN GITMEDI?" (tasarim C). Protokol no ya da hasta adi; son taramadan
    // cevaplanir -- Pusula'ya gitmez.
    public record NedenSonucu(string Sorgu, List<(PendingProtocol? Bekleyen, PendingProtocol? Takili)> Bulunan,
                              int? ProtokolNo, int Toplam);

    public IActionResult OnGetNeden(string? q)
    {
        var s = pendingWork.SonSonuc;
        if (s is null) return Content("Önce hesaplayın.");
        q = (q ?? "").Trim();
        if (q.Length == 0) return Content("");

        var bekleyen = s.Protokoller.ToDictionary(p => p.Protokol.ProtokolId);
        var takili = s.TakilanProtokoller.ToDictionary(p => p.Protokol.ProtokolId);
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        int? no = int.TryParse(q, out var n) ? n : null;
        bool Uyar(PendingProtocol p) => no is { } x
            ? p.Protokol.ProtokolId == x
            : tr.CompareInfo.IndexOf(p.Protokol.HastaAdiSoyadi, q, System.Globalization.CompareOptions.IgnoreCase) >= 0
              || (p.Protokol.Fin ?? "").Equals(q, StringComparison.OrdinalIgnoreCase);

        var idler = s.Protokoller.Where(Uyar).Concat(s.TakilanProtokoller.Where(Uyar))
            .Select(p => p.Protokol.ProtokolId).Distinct().ToList();
        var bulunan = idler.Take(10)
            .Select(id => (bekleyen.GetValueOrDefault(id), takili.GetValueOrDefault(id)))
            .ToList();
        return Partial("_BekleyenNeden", new NedenSonucu(q, bulunan, no, idler.Count));
    }

    // Tarih secici (_TarihSecici) gizli From/To alanlariyla gonderiyor.
    public async Task<IActionResult> OnPostYenileAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        Baslangic ??= from;
        Bitis ??= to;
        await AralikHesaplaAsync(ct);

        // Bitis gun sonuna kadar dahil -> ertesi gunun 00:00'i (haric) gonderiliyor.
        // KIRPILMIYOR (2026-10-27): sebep dokumu ve protokol sorgusu TUM kumeye bakmali;
        // kirpma yalnizca bellekte siralama, ag cagrisi yok.
        await pendingWork.RefreshAsync(
            scanDays: null,
            maxProtocols: int.MaxValue,
            ct: ct,
            fromLocalOverride: EtkinBaslangic.ToDateTime(TimeOnly.MinValue),
            toLocalExclusive: EtkinBitis.AddDays(1).ToDateTime(TimeOnly.MinValue));

        return RedirectToPage(new { Baslangic = EtkinBaslangic.ToString("yyyy-MM-dd"), Bitis = EtkinBitis.ToString("yyyy-MM-dd") });
    }

    private async Task AralikHesaplaAsync(CancellationToken ct)
    {
        var bugun = DateOnly.FromDateTime(DateTime.Now);
        EtkinBaslangic = Baslangic
            ?? DateOnly.FromDateTime(await PendingWorkService.TaramaBaslangiciAsync(settings, ct));
        EtkinBitis = Bitis ?? bugun;

        // Ters aralik girilirse sessizce bos liste gostermek yerine duzelt ve soyle --
        // kullanici "hic bekleyen is yok" sanmasin.
        if (EtkinBitis < EtkinBaslangic)
        {
            (EtkinBaslangic, EtkinBitis) = (EtkinBitis, EtkinBaslangic);
            UyariMesaji = "Bitiş tarihi başlangıçtan önceydi, tarihler yer değiştirildi.";
        }
    }
}
