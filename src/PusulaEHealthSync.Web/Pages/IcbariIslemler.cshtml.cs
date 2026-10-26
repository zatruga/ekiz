using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Export;
using PusulaEHealthSync.Reporting;
using static PusulaEHealthSync.Reporting.IcbariKapsamService;

namespace PusulaEHealthSync.Web.Pages;

// ICBARI ISLEMLER (2026-10-26) -- Genel Bakis'taki icbari rakamlarinin acildigi sayfa.
//
// KULLANICI: "rakamlara odaklanalim ve tiklayinca filtre edilmis sayfaya gidelim."
// Kume ve kural IcbariKapsamService'te -- Genel Bakis ile ORTAK. Kartta yazan sayi ile bu
// sayfada ayni filtreyle listelenen satir sayisi ayni kaynaktan cikiyor.
//
// Tarih araligi protokolun Pusula'daki ACILIS tarihi (Genel Bakis'in icbari bolumuyle ayni).
// Durum CANLI ortama gore -- Genel Bakis yalnizca canli gonderimleri anlatiyor.
public class IcbariIslemlerModel(IcbariKapsamService icbari, ILogger<IcbariIslemlerModel> logger) : PageModel
{
    private const int SayfaBoyutu = 100;

    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    // gitti | gitmedi (eksik+hatali+denenmedi) | denenmedi | eksik | hatali
    [BindProperty(SupportsGet = true)] public string? Durum { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sebep { get; set; }
    [BindProperty(SupportsGet = true)] public string? Hizmet { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly EffectiveTo { get; private set; }
    public IcbariOzeti Ozet { get; private set; } = IcbariOzeti.Bos;
    public List<Kalem> Satirlar { get; private set; } = [];
    public int ListelenenSayi { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public bool HasNextPage { get; private set; }
    public bool PusulaHatasi { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var liste = await SuzulmusAsync(ct);
        PageNumber = P < 1 ? 1 : P;
        ListelenenSayi = liste.Count;
        Satirlar = liste.Skip((PageNumber - 1) * SayfaBoyutu).Take(SayfaBoyutu).ToList();
        HasNextPage = liste.Count > PageNumber * SayfaBoyutu;
    }

    // Faturalama ekibine: suzulmus listenin TAMAMI (sayfalama yok).
    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        var liste = await SuzulmusAsync(ct);
        var sutunlar = new List<XlsxWriter.Sutun>
        {
            new("Protokol", 12, Sayi: true),
            new("Hasta", 30),
            new("Hizmet", 44),
            new("İcbari karşılığı", 44),
            new("Durum", 16),
            new("Sebep", 34),
            new("TRƏS mesajı", 70),
        };
        var satirlar = liste.Select(k => new string?[]
        {
            k.Islem.ProtokolId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            k.Islem.PatientName, HizmetAdi(k.Islem), k.Islem.IcbariAdi,
            DurumAdi(k.Durum), k.Sebep, k.Mesaj,
        });
        var bayt = XlsxWriter.Olustur("İcbari İşlemler", sutunlar, satirlar);
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"icbari-islemler-{EffectiveFrom:yyyyMMdd}-{EffectiveTo:yyyyMMdd}.xlsx");
    }

    private async Task<List<Kalem>> SuzulmusAsync(CancellationToken ct)
    {
        var bugun = DateOnly.FromDateTime(DateTime.Today);
        EffectiveFrom = From ?? bugun.AddDays(-6);
        EffectiveTo = To ?? bugun;

        List<Kalem> kalemler;
        try
        {
            kalemler = await icbari.KalemlerAsync(
                EffectiveFrom.ToDateTime(TimeOnly.MinValue), EffectiveTo.AddDays(1).ToDateTime(TimeOnly.MinValue),
                "Live", ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Icbari Islemler: liste Pusula'dan okunamadi.");
            PusulaHatasi = true;
            return [];
        }

        // Kartlar SUZGECSIZ ozet -- hangi filtre secili olursa olsun araligin tamami.
        Ozet = Ozetle(kalemler, hizmetTavan: 0);

        var aranan = Q?.Trim().ToUpper(Tr);
        return kalemler
            .Where(k => Durum switch
            {
                null or "" => true,
                "gitmedi" => k.Durum != IcbariKapsamService.Durum.Gitti,
                _ => DurumAnahtari(k.Durum) == Durum,
            })
            .Where(k => string.IsNullOrEmpty(Sebep) || k.Sebep == Sebep)
            .Where(k => string.IsNullOrEmpty(Hizmet) || HizmetAdi(k.Islem) == Hizmet)
            .Where(k => string.IsNullOrEmpty(aranan)
                || k.Islem.ProtokolId.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(aranan)
                || (k.Islem.PatientName?.ToUpper(Tr).Contains(aranan) ?? false)
                || HizmetAdi(k.Islem).ToUpper(Tr).Contains(aranan))
            // Gitmeyenler once, en yeni protokol once.
            .OrderBy(k => k.Durum == IcbariKapsamService.Durum.Gitti)
            .ThenByDescending(k => k.Islem.ProtokolId)
            .ToList();
    }

    private static readonly System.Globalization.CultureInfo Tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    public static string DurumAdi(IcbariKapsamService.Durum d) => d switch
    {
        IcbariKapsamService.Durum.Gitti => "Gönderildi",
        IcbariKapsamService.Durum.Eksik => "Eksik verili",
        IcbariKapsamService.Durum.Hatali => "Hatalı",
        _ => "Henüz denenmedi",
    };
}
