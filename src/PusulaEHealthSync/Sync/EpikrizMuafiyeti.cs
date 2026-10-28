using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// BOS EPIKRIZ, MUAF BOLUM (2026-10-28). Bos epikriz eksik sayilir (kullanici karari
// 2026-10-28), ama Ayarlar'daki bolumlerde (varsayilan Laboratoriya, Radiologiya) protokol
// yalnizca tetkik icin acilir ve epikriz beklenmez. Bu bolumlerin "Epikriz metni bos"
// atlamasi HIC YOKMUS gibi davranilir -- ne eksik ne basarili; protokol diger kalemlerine
// gore siniflanir.
//
// UC YERDE AYNI KURAL: tarama (PendingWorkService -- Bekleyen Isler, Genel Bakis hatti),
// Genel Bakis'in donem tablolari (epikriz-doktor dahil) ve Icbari karti. Biri digerinden
// farkli sayarsa ayni protokol iki ekranda iki ayri sinifta gorunur.
public static class EpikrizMuafiyeti
{
    // CompositionMapper'in bos icerikte yazdigi mesajin basi.
    public const string BosMesajBasi = "Epikriz metni boş";

    public static bool BosEpikriz(string resourceType, SyncStatus durum, string? mesaj) =>
        resourceType == "Composition" && durum == SyncStatus.Skipped
        && mesaj is not null && mesaj.StartsWith(BosMesajBasi, StringComparison.Ordinal);

    public static async Task<HashSet<string>> BolumleriAsync(SettingsStore settings, CancellationToken ct)
    {
        var ham = await settings.GetStringAsync(
            SettingsStore.EpikrizMuafBolumlerKey, SettingsStore.EpikrizMuafBolumlerDefault, ct);
        return Ayristir(ham);
    }

    public static HashSet<string> Ayristir(string? ham) =>
        (ham ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
}
