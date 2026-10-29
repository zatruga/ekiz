using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// OTOMATIK GONDERIM KURALLARI: KURUM / BOLUM / DOKTOR (2026-10-29, kullanici: "gonderilecek
// kurum - brans - doktor secimi ve bu kurallara gore gonderim yapacagiz").
//
// Her boyut bagimsiz bir suzgec: Tumu (suzgec yok), Yalniz (yalniz isaretliler gider),
// Haric (isaretliler gitmez). Uc boyut VE ile birlesir -- protokol ucunden de gecmeli.
//
// YATAN ANAHTARIYLA AYNI YER (bkz. PendingWorkService.YatanOtomatikDisiSebep): uygunluk
// kurali DEGIL, uygun protokolu otomatik donguden ayiran ayri bir adim. Elle gonderim
// (Protokol, Protokol Listesi, Yatan Hastalar) bu listeden gecmedigi icin etkilenmez.
//
// NEDEN ID, AD DEGIL: doktor adlari tekrarlanabiliyor, bolum ve kurum adlari Pusula'da
// degistirilebiliyor. Ekran adlari Id'den cozuyor.
//
// KURUM OLCUTU Hasta.Protokol.AKKurumId -- Icbari karti da ayni alana bakiyor
// (bkz. PusulaRepository.IcbariKurumId). Bos alan "kurumsuz (ucretli)" demek, Id'si 0.
public enum KuralModu { Tumu, Yalniz, Haric }

public sealed record GonderimKurali(
    KuralModu KurumModu, IReadOnlySet<int> Kurumlar,
    KuralModu BolumModu, IReadOnlySet<int> Bolumler,
    KuralModu DoktorModu, IReadOnlySet<int> Doktorlar)
{
    public const int KurumsuzId = 0;

    // Bekleyen Isler'in sebep dokumu bu on eki tanir (bkz. BekleyenDokum.KuralSebebi).
    public const string SebepBasi = "Otomatik gönderim kuralı dışı";

    public static GonderimKurali Serbest { get; } =
        new(KuralModu.Tumu, new HashSet<int>(), KuralModu.Tumu, new HashSet<int>(), KuralModu.Tumu, new HashSet<int>());

    public bool Etkin => KurumModu != KuralModu.Tumu || BolumModu != KuralModu.Tumu || DoktorModu != KuralModu.Tumu;

    // null = protokol kurallardan gecti; aksi halde ilk takildigi boyutun sebebi.
    public string? Sebep(ProtokolListItem p)
    {
        if (!Gecer(KurumModu, Kurumlar, p.KurumId ?? KurumsuzId))
            return $"{SebepBasi} — kurum: {p.KurumAdi ?? "kurumsuz (ücretli)"} (Ayarlar). Elle gönderilebilir";
        if (!Gecer(BolumModu, Bolumler, p.BolumId))
            return $"{SebepBasi} — bölüm: {p.BolumAdi ?? "(bölümsüz)"} (Ayarlar). Elle gönderilebilir";
        if (!Gecer(DoktorModu, Doktorlar, p.DoktorId))
            return $"{SebepBasi} — doktor: {p.DoktorAdiSoyadi ?? "(doktorsuz)"} (Ayarlar). Elle gönderilebilir";
        return null;
    }

    // Yalniz modunda Id'si bos olan protokol GECMEZ (secilmemis bir seyi "secili" saymak olmaz);
    // Haric modunda gecer.
    private static bool Gecer(KuralModu mod, IReadOnlySet<int> secili, int? id) => mod switch
    {
        KuralModu.Yalniz => id is { } i && secili.Contains(i),
        KuralModu.Haric => id is not { } h || !secili.Contains(h),
        _ => true,
    };

    public static async Task<GonderimKurali> YukleAsync(SettingsStore settings, CancellationToken ct) => new(
        Mod(await settings.GetStringAsync(SettingsStore.AutoSendKuralKurumModKey, "", ct)),
        Idler(await settings.GetStringAsync(SettingsStore.AutoSendKuralKurumIdlerKey, "", ct)),
        Mod(await settings.GetStringAsync(SettingsStore.AutoSendKuralBolumModKey, "", ct)),
        Idler(await settings.GetStringAsync(SettingsStore.AutoSendKuralBolumIdlerKey, "", ct)),
        Mod(await settings.GetStringAsync(SettingsStore.AutoSendKuralDoktorModKey, "", ct)),
        Idler(await settings.GetStringAsync(SettingsStore.AutoSendKuralDoktorIdlerKey, "", ct)));

    public static KuralModu Mod(string? ham) =>
        Enum.TryParse<KuralModu>(ham, ignoreCase: true, out var m) ? m : KuralModu.Tumu;

    public static HashSet<int> Idler(string? ham) =>
        (ham ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var i) ? i : (int?)null)
            .Where(i => i is >= 0)
            .Select(i => i!.Value)
            .ToHashSet();

    public static string Yaz(IEnumerable<int> idler) => string.Join(";", idler.Distinct().Order());
}
