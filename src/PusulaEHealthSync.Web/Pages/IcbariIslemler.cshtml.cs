using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Export;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
using PusulaEHealthSync.Sync;
using static PusulaEHealthSync.Reporting.IcbariKapsamService;

namespace PusulaEHealthSync.Web.Pages;

// ICBARI ISLEMLER (2026-10-26) -- Genel Bakis'taki icbari rakamlarinin acildigi sayfa;
// menude de var.
//
// KULLANICI: "rakamlara odaklanalim ve tiklayinca filtre edilmis sayfaya gidelim" ->
// "Icbari islemler sayfasini sevdim; bu alanda protokol odakli olsun, sayilara tiklayinca
// acilan ekranda gonderim secenekleri de olsun."
//
// BIRIM PROTOKOL: her satir bir protokol, icinde icbari islemleri. Sinif kurali
// IcbariKapsamService.ProtokolDurumu -- Genel Bakis ile ORTAK; kartta yazan sayi ile bu
// sayfada ayni filtreyle listelenen protokol sayisi ayni kaynaktan cikiyor.
//
// Tarih araligi protokolun Pusula'daki ACILIS tarihi. Durum CANLI ortama gore (Genel Bakis
// gibi). Gonderim ise Ayarlar'daki AKTIF ortama gider -- ikisi farkliysa ekran uyariyor.
public class IcbariIslemlerModel(
    IcbariKapsamService icbari, PusulaRepository repository, ProtocolFullSyncService protocolFullSync,
    SettingsStore settings, SyncLogStore syncLog, ILogger<IcbariIslemlerModel> logger) : PageModel
{
    private const int SayfaBoyutu = 50;
    private const int TopluTavan = 25;

    [BindProperty(SupportsGet = true)] public DateOnly? From { get; set; }
    [BindProperty(SupportsGet = true)] public DateOnly? To { get; set; }
    // gitti | gitmedi | denenmedi | eksik | hatali
    [BindProperty(SupportsGet = true)] public string? Durum { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sebep { get; set; }
    [BindProperty(SupportsGet = true)] public string? Hizmet { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    [TempData] public string? IslemMesaji { get; set; }
    [TempData] public bool IslemBasarili { get; set; }

    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly EffectiveTo { get; private set; }
    public IcbariOzeti Ozet { get; private set; } = IcbariOzeti.Bos;
    public List<Satir> Satirlar { get; private set; } = [];

    // SATIR = Aktivite Akisi'ndaki satirin AYNISI (kullanici 2026-10-26: "icbari islemler
    // aktivite akisindaki gibi olmali, protokol detaylari vs ayni"). Akis: protokolun TUM
    // gonderim zinciri (Live) -- YatanHastalarModel.AkisKur ile ayni katlama. Icbari: o
    // protokolun icbari kalemleri ve icbari sinifi. Akis yalniz sayfadaki 50 protokol icin
    // kuruluyor; kartlar ve suzgec Icbari'den.
    public record Satir(ProtokolKalemi Icbari, AktiviteGrubu Akis, DateTime? SonGonderimUtc)
    {
        public ProtokolListItem? P => Akis.Protokol;
    }
    public int ListelenenSayi { get; private set; }
    public int ListelenenIslem { get; private set; }
    public int PageNumber { get; private set; } = 1;
    public bool HasNextPage { get; private set; }
    public bool PusulaHatasi { get; private set; }
    public string AktifOrtam { get; private set; } = SettingsStore.EHealthEnvironmentDefault;

    public async Task OnGetAsync(CancellationToken ct)
    {
        AktifOrtam = await settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey,
            SettingsStore.EHealthEnvironmentDefault, ct);
        var liste = await SuzulmusAsync(ct);
        PageNumber = P < 1 ? 1 : P;
        ListelenenSayi = liste.Count;
        ListelenenIslem = liste.Sum(p => p.IslemSayisi);
        HasNextPage = liste.Count > PageNumber * SayfaBoyutu;
        Satirlar = await SatirlariKurAsync(liste.Skip((PageNumber - 1) * SayfaBoyutu).Take(SayfaBoyutu).ToList(), ct);
    }

    private async Task<List<Satir>> SatirlariKurAsync(List<ProtokolKalemi> sayfa, CancellationToken ct)
    {
        if (sayfa.Count == 0) return [];
        var ids = sayfa.Select(p => p.ProtokolId).ToList();
        Dictionary<int, ProtokolListItem> protokoller;
        try
        {
            protokoller = await repository.GetProtokollerByIdsAsync(ids, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Icbari Islemler: protokol bilgileri Pusula'dan okunamadi.");
            protokoller = [];
        }

        var ozet = await syncLog.QueryOzetByProtokolIdsAsync(
            ids, protokoller.Values.Select(p => p.HastaId).Where(h => h > 0).Distinct().ToList(), null, "Live", ct);
        var protokolBasina = ozet.Where(o => o.ProtokolId is not null).ToLookup(o => o.ProtokolId!.Value);
        var hastaBasina = ozet.Where(o => o.ResourceType == "Patient").ToLookup(o => o.PusulaId);
        var sonIdler = ids.Select(id => protokolBasina[id].Select(o => o.SonId).DefaultIfEmpty(0).Max()).ToList();
        var zamanlar = await syncLog.GetZamanlarAsync(sonIdler.Where(i => i > 0).ToList(), ct);

        return sayfa.Select((p, i) =>
        {
            var protokol = protokoller.GetValueOrDefault(p.ProtokolId)
                ?? new ProtokolListItem { ProtokolId = p.ProtokolId, HastaAdi = p.Hasta };
            var kaynak = protokolBasina[p.ProtokolId]
                .Concat(protokol.HastaId > 0 ? hastaBasina[protokol.HastaId] : []);
            return new Satir(p, YatanHastalarModel.AkisKur(protokol, kaynak),
                zamanlar.TryGetValue(sonIdler[i], out var z) ? z : null);
        }).ToList();
    }

    // Faturalama ekibine: suzulmus protokollerin ISLEMLERI (satir = islem), sayfalama yok.
    public async Task<IActionResult> OnGetExcelAsync(CancellationToken ct)
    {
        var liste = await SuzulmusAsync(ct);
        var sutunlar = new List<XlsxWriter.Sutun>
        {
            new("Protokol", 12, Sayi: true),
            new("Hasta", 30),
            new("Protokol durumu", 18),
            new("Hizmet", 44),
            new("İcbari karşılığı", 44),
            new("İşlem durumu", 16),
            new("Sebep", 34),
            new("TRƏS mesajı", 70),
        };
        var satirlar = liste.SelectMany(p => p.Kalemler.Select(k => new string?[]
        {
            p.ProtokolId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            p.Hasta, DurumAdi(p.Durum), HizmetAdi(k.Islem), k.Islem.IcbariAdi,
            DurumAdi(k.Durum), k.Durum == IcbariKapsamService.Durum.Gitti ? null : k.Sebep, k.Mesaj,
        }));
        var bayt = XlsxWriter.Olustur("İcbari İşlemler", sutunlar, satirlar);
        return File(bayt, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"icbari-islemler-{EffectiveFrom:yyyyMMdd}-{EffectiveTo:yyyyMMdd}.xlsx");
    }

    // ---------------------------------------------------------------- GONDERIM
    // Otomatik dongunun yaptiginin AYNISI (ProtocolFullSyncService.SyncAllAsync) -- protokolun
    // tamam zinciri gider, icbari islemler de onun icinde.
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
        Sonuc(kotu == 0, $"{hedefler.Count} protokol gönderildi: {iyi} başarılı, {kotu} başarısız. Güncel durum aşağıda.");
        return GeriDon();
    }

    private async Task<(bool Basarili, string Mesaj)> GonderAsync(int protokolId, CancellationToken ct)
    {
        var protokol = await repository.GetProtokolByIdAsync(protokolId, ct);
        if (protokol is null) return (false, $"Protokol {protokolId} Pusula'da bulunamadı.");
        try
        {
            var sonuc = await protocolFullSync.SyncAllAsync(protokol, ct);
            return (sonuc.EncounterStatus != SyncStatus.Failed,
                $"Protokol {protokolId} gönderildi (muayene: {sonuc.EncounterStatus}).");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Icbari Islemler: protokol {Id} gonderilemedi.", protokolId);
            return (false, $"Protokol {protokolId} gönderilemedi: {ex.Message}");
        }
    }

    private void Sonuc(bool basarili, string mesaj)
    {
        IslemBasarili = basarili;
        IslemMesaji = mesaj;
    }

    private IActionResult GeriDon() => RedirectToPage("/IcbariIslemler", new
    {
        From = From?.ToString("yyyy-MM-dd"), To = To?.ToString("yyyy-MM-dd"),
        Durum, Sebep, Hizmet, Q, P = P < 1 ? 1 : P,
    });

    // ---------------------------------------------------------------- SUZME
    private async Task<List<ProtokolKalemi>> SuzulmusAsync(CancellationToken ct)
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

        // SEBEP / HIZMET VARSA KALEM DUZEYINDE: o sebepte (ya da o hizmette) durumu tutan
        // bir islemi olan protokoller -- Genel Bakis'taki sebep/hizmet satirlarinin saydigi
        // kume. Yoksa PROTOKOL SINIFI duzeyinde -- durum kartlarinin saydigi kume.
        var kalemSuzgeci = !string.IsNullOrEmpty(Sebep) || !string.IsNullOrEmpty(Hizmet);
        bool KalemDurumuUyar(Kalem k) => Durum switch
        {
            null or "" => true,
            "gitmedi" => k.Durum != IcbariKapsamService.Durum.Gitti,
            _ => DurumAnahtari(k.Durum) == Durum,
        };
        bool ProtokolDurumuUyar(ProtokolKalemi p) => Durum switch
        {
            null or "" => true,
            "gitmedi" => p.Durum != IcbariKapsamService.Durum.Gitti,
            _ => DurumAnahtari(p.Durum) == Durum,
        };

        var aranan = Q?.Trim().ToUpper(Tr);
        return Protokollere(kalemler)
            .Where(p => kalemSuzgeci
                ? p.Kalemler.Any(k => (string.IsNullOrEmpty(Sebep) || k.Sebep == Sebep)
                                   && (string.IsNullOrEmpty(Hizmet) || HizmetAdi(k.Islem) == Hizmet)
                                   && KalemDurumuUyar(k))
                : ProtokolDurumuUyar(p))
            .Where(p => string.IsNullOrEmpty(aranan)
                || p.ProtokolId.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains(aranan)
                || (p.Hasta?.ToUpper(Tr).Contains(aranan) ?? false)
                || p.Kalemler.Any(k => HizmetAdi(k.Islem).ToUpper(Tr).Contains(aranan)))
            // Gitmeyeni cok olan once -- kazanc kapisi en buyuk olan ustte.
            .OrderByDescending(p => p.Gitmedi)
            .ThenByDescending(p => p.ProtokolId)
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

    // Aktivite Akisi'nin renk sinifi (akis-kare--*, akis-satir--*).
    public static string AkisCss(IcbariKapsamService.Durum d) => d switch
    {
        IcbariKapsamService.Durum.Gitti => "ok",
        IcbariKapsamService.Durum.Eksik => "eksik",
        IcbariKapsamService.Durum.Hatali => "hata",
        _ => "yok",
    };

    public static string DurumCss(IcbariKapsamService.Durum d) => d switch
    {
        IcbariKapsamService.Durum.Gitti => "success",
        IcbariKapsamService.Durum.Eksik => "warning",
        IcbariKapsamService.Durum.Hatali => "danger",
        _ => "neutral",
    };
}
