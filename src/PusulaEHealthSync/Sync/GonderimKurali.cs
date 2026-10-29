using System.Text.Json;
using System.Text.Json.Serialization;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// OTOMATIK GONDERIM KURALLARI: KURUM BASINA BOLUM / DOKTOR (2026-10-29).
//
// KULLANICI: "gonderilecek kurum - brans - doktor secimi ve bu kurallara gore gonderim
// yapacagiz" + "icbari sigorta 3 brans gitsin ama baska bir kurumda 5, diger bir kurumda
// tumu gitsin diyebilir miyim?"
//
// Bu yuzden KURAL TABLOSU: her kurum icin bir satir, bir de "diger kurumlar" satiri.
// Protokolun kurumunun satiri varsa o satir, yoksa Diger uygulanir. Satir:
//   Gonderme   -> o kurumun protokolleri hic otomatik gitmez
//   Bolum/Doktor -> Tumu (suzgec yok) / Yalniz (yalniz isaretliler) / Haric (isaretliler gitmez),
//                   ikisi VE ile birlesir.
// Bos tablo + varsayilan Diger = suzgec yok (kural eklenmeden onceki davranis).
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
//
// SAKLAMA: tek ayar anahtarinda JSON (SettingsStore.AutoSendKuralTabloKey). Tablo kucuk ve
// her kayitta TAMAMI yaziliyor -- yarim kalmis bir kural tablosu olusamaz.
public enum KuralModu { Tumu, Yalniz, Haric }

public sealed record KuralSatiri
{
    public int? KurumId { get; init; }   // null = diger kurumlar
    public bool Gonderme { get; init; }
    public KuralModu BolumModu { get; init; }
    public HashSet<int> Bolumler { get; init; } = [];
    public KuralModu DoktorModu { get; init; }
    public HashSet<int> Doktorlar { get; init; } = [];

    [JsonIgnore]
    public bool Serbest => !Gonderme && BolumModu == KuralModu.Tumu && DoktorModu == KuralModu.Tumu;
}

public sealed record GonderimKurali(IReadOnlyList<KuralSatiri> Satirlar, KuralSatiri Diger)
{
    public const int KurumsuzId = 0;

    // Bekleyen Isler'in sebep dokumu bu on eki tanir (bkz. BekleyenDokum.KuralSebebi).
    public const string SebepBasi = "Otomatik gönderim kuralı dışı";

    public static GonderimKurali Serbest { get; } = new([], new KuralSatiri());

    public bool Etkin => Satirlar.Count > 0 || !Diger.Serbest;

    // Protokolun kurumu bilinmeden karar verilebilir mi? Kurum satiri yoksa herkes Diger'e
    // tabi -- kurumu okumaya gerek yok (bkz. Aktivite, protokol listesi kurumu tasimiyor).
    public bool KurumGerekli => Satirlar.Count > 0;

    public KuralSatiri? Satir(int kurumId) => Satirlar.FirstOrDefault(s => s.KurumId == kurumId);

    // null = protokol kurallardan gecti; aksi halde takildigi yerin sebebi.
    public string? Sebep(ProtokolListItem p)
    {
        var kurumId = p.KurumId ?? KurumsuzId;
        var kurumAdi = p.KurumAdi ?? (kurumId == KurumsuzId ? "kurumsuz (ücretli)" : $"kurum #{kurumId}");
        var satir = Satir(kurumId);
        var kuralAdi = satir is null ? "diğer kurumlar kuralı" : $"{kurumAdi} kuralı";
        satir ??= Diger;

        if (satir.Gonderme)
            return $"{SebepBasi} — kurum: {kurumAdi} otomatik gönderilmiyor ({kuralAdi}, Ayarlar). Elle gönderilebilir";
        if (!Gecer(satir.BolumModu, satir.Bolumler, p.BolumId))
            return $"{SebepBasi} — bölüm: {p.BolumAdi ?? "(bölümsüz)"} ({kuralAdi}, Ayarlar). Elle gönderilebilir";
        if (!Gecer(satir.DoktorModu, satir.Doktorlar, p.DoktorId))
            return $"{SebepBasi} — doktor: {p.DoktorAdiSoyadi ?? "(doktorsuz)"} ({kuralAdi}, Ayarlar). Elle gönderilebilir";
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

    // Kurum satiri ekler ya da degistirir (KurumId null ise Diger'i).
    public GonderimKurali Ile(KuralSatiri satir) => satir.KurumId is { } id
        ? this with { Satirlar = [.. Satirlar.Where(s => s.KurumId != id), satir] }
        : this with { Diger = satir };

    public GonderimKurali Olmadan(int kurumId) => this with { Satirlar = [.. Satirlar.Where(s => s.KurumId != kurumId)] };

    // ---- Saklama ----
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private sealed record Kayit(List<KuralSatiri>? Satirlar, KuralSatiri? Diger);

    public static async Task<GonderimKurali> YukleAsync(SettingsStore settings, CancellationToken ct) =>
        Ayristir(await settings.GetStringAsync(SettingsStore.AutoSendKuralTabloKey, "", ct));

    // Bos ya da bozuk metin = suzgec yok. Metni yalnizca Ayarlar sayfasi yaziyor.
    public static GonderimKurali Ayristir(string? ham)
    {
        if (string.IsNullOrWhiteSpace(ham)) return Serbest;
        try
        {
            var k = JsonSerializer.Deserialize<Kayit>(ham, Json);
            if (k is null) return Serbest;
            var satirlar = (k.Satirlar ?? [])
                .Where(s => s.KurumId is >= 0)
                .GroupBy(s => s.KurumId).Select(g => g.Last())
                .ToList();
            return new(satirlar, (k.Diger ?? new KuralSatiri()) with { KurumId = null });
        }
        catch (JsonException) { return Serbest; }
    }

    public string Yaz() => JsonSerializer.Serialize(
        new Kayit([.. Satirlar.OrderBy(s => s.KurumId)], Diger), Json);

    public static KuralModu Mod(string? ham) =>
        Enum.TryParse<KuralModu>(ham, ignoreCase: true, out var m) ? m : KuralModu.Tumu;
}
