using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Reporting;

// Gun sonu raporunu SyncLog'dan hesaplar. Bakanliga ag cagrisi YAPMAZ; Pusula'ya yalnizca
// protokol numarasi cozmek icin SELECT atar.
public class GunSonuRaporService(
    SyncLogStore syncLog,
    PusulaRepository repository,
    SettingsStore settings,
    PendingWorkService pendingWork,
    ILogger<GunSonuRaporService> logger)
{
    // Hatali kalem listesinin tavani. Asilirsa rapor "ilk N" diyip toplami ayrica yazar --
    // 4.000 satirlik bir e-posta, kimsenin okumadigi bir e-postadir.
    public const int HataliKalemTavani = 300;

    public async Task<GunSonuRaporu> OlusturAsync(DateOnly gun, CancellationToken ct = default)
    {
        var kayitlar = await syncLog.GetGunKayitlariAsync(gun, ct);
        var ortam = await settings.GetStringAsync(
            SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);
        var otomatikAcik = await settings.GetBoolAsync(
            SettingsStore.AutoSendEncounterEnabledKey, false, ct);

        // -- UC AYRI IS, AYNI TABLODA ------------------------------------------------------
        // SyncLog gonderimi, silmeyi ve dogrulamayi ayni yere yaziyor ve ucunde de
        // Status=Success ayni gorunuyor. Ayirmazsak "412 basarili gonderim" cumlesi iptal
        // senkronunun sildigi kayitlari da sayar -- raporun en temel sayisi yanlis olur.
        //
        // DOGRULAMA ($validate) RAPORDAN TAMAMEN CIKIYOR: bakanliga hicbir sey yazmayan bir
        // deneme, "bugun ne gonderdik" sorusunun cevabi degil.
        var silmeler = kayitlar.Where(k => k.Operation == SyncOperation.Delete).ToList();
        var gonderimler = kayitlar
            .Where(k => k.Operation != SyncOperation.Delete && k.Operation != SyncOperation.Validate)
            .ToList();

        var basarili = gonderimler.Count(k => k.Status == SyncStatus.Success);
        var hatali = gonderimler.Count(k => k.Status == SyncStatus.Failed);
        var atlanan = gonderimler.Count(k => k.Status == SyncStatus.Skipped);

        // -- PROTOKOL COZUMU --------------------------------------------------------------
        // SyncLog'da protokol bagi yok; her kaynak tipi icin id'leri Pusula'ya sorup
        // cozuyoruz (bkz. PusulaRepository.ProtokolIdleriniCozAsync).
        var protokolEslemesi = new Dictionary<(string, int), int>();
        foreach (var grup in gonderimler.GroupBy(k => k.ResourceType))
        {
            var idler = grup.Select(k => k.PusulaId).Distinct().ToList();
            try
            {
                var cozum = await repository.ProtokolIdleriniCozAsync(grup.Key, idler, ct);
                foreach (var kv in cozum) protokolEslemesi[(grup.Key, kv.Key)] = kv.Value;
            }
            catch (Exception ex)
            {
                // Protokol cozumu RAPORU DUSURMEMELI: sayilar yine dogru, yalnizca protokol
                // numarasi yazilamaz. Raporsuz kalmak, protokol numarasiz rapordan kotu.
                logger.LogWarning(ex,
                    "Gun sonu raporu: {Tip} icin protokol numaralari cozulemedi, kalemler "
                    + "protokolsuz gosterilecek.", grup.Key);
            }
        }

        int? ProtokolBul(SyncLogEntry k) =>
            protokolEslemesi.TryGetValue((k.ResourceType, k.PusulaId), out var p) ? p : null;

        // -- PROTOKOL BAZLI KOVALAR -------------------------------------------------------
        // Bir protokol: en az bir hatasi varsa HATALI, hatasi yok ama atlanani varsa EKSIK
        // VERILI, ikisi de yoksa BASARILI. Sira onemli -- hata atlanani ezer, cunku kisinin
        // once hatayi gormesi gerekiyor.
        var protokolDurumlari = new Dictionary<int, (bool Hata, bool Atlama, bool Basari)>();
        foreach (var k in gonderimler)
        {
            if (ProtokolBul(k) is not { } pid) continue;
            var mevcut = protokolDurumlari.GetValueOrDefault(pid);
            protokolDurumlari[pid] = (
                mevcut.Hata || k.Status == SyncStatus.Failed,
                mevcut.Atlama || k.Status == SyncStatus.Skipped,
                mevcut.Basari || k.Status == SyncStatus.Success);
        }

        var hataliProtokol = protokolDurumlari.Count(p => p.Value.Hata);
        var eksikProtokol = protokolDurumlari.Count(p => !p.Value.Hata && p.Value.Atlama);
        var basariliProtokol = protokolDurumlari.Count(p => !p.Value.Hata && !p.Value.Atlama && p.Value.Basari);

        // -- KAYNAK TIPI KIRILIMI ---------------------------------------------------------
        var kaynakKirilimi = gonderimler
            .GroupBy(k => SyncLogEntry.ResourceTypeLabel(k.ResourceType))
            .Select(g => new GunSonuKaynakSatiri(
                g.Key,
                g.Count(k => k.Status == SyncStatus.Success),
                g.Count(k => k.Status == SyncStatus.Failed),
                g.Count(k => k.Status == SyncStatus.Skipped)))
            .OrderByDescending(s => s.Toplam)
            .ToList();

        // -- HATA KATEGORILERI ------------------------------------------------------------
        // Siniflandirma SyncLogEntry.ErrorCategory'de -- rapor kendi kategorilerini
        // yazmiyor, yoksa ekrandaki kategorilerle rapordaki kategoriler zamanla ayrisir.
        var hataGruplari = gonderimler
            .Where(k => k.Status is SyncStatus.Failed or SyncStatus.Skipped)
            .GroupBy(k => SyncLogEntry.ErrorCategory(k.Message))
            .Select(g => new GunSonuHataGrubu(
                g.Key.Label, g.Key.Description, g.Count(),
                SyncLogEntry.FriendlyError(g.First().Message)))
            .OrderByDescending(g => g.Adet)
            .ToList();

        // -- HATALI KALEM LISTESI ---------------------------------------------------------
        // Hatalar once (atlananlardan daha acil), sonra protokol numarasina gore -- ayni
        // protokolun satirlari yan yana dussun, kisi bir protokolu bir kez acsin.
        var hataliHepsi = gonderimler
            .Where(k => k.Status is SyncStatus.Failed or SyncStatus.Skipped)
            .Select(k => new GunSonuHataliKalem(
                ProtokolBul(k),
                SyncLogEntry.ResourceTypeLabel(k.ResourceType),
                k.PusulaId,
                SyncLogEntry.FriendlyError(k.Message),
                k.Status == SyncStatus.Skipped,
                AzTime.ToLocal(k.CreatedAtUtc)))
            .OrderBy(k => k.Atlandi)
            .ThenBy(k => k.ProtokolId ?? int.MaxValue)
            .ThenBy(k => k.ZamanYerel)
            .ToList();

        // -- ILERIYE DONUK ----------------------------------------------------------------
        // Son taramanin SONUCU kullaniliyor, YENI TARAMA BASLATILMIYOR: tam tarama canli
        // veride ~18 sn ve rapor saatinde saatlik tur zaten taramayi tazelemis olur. Rapor
        // ugruna Pusula'ya ek yuk bindirmek yanlis takas.
        var sonTarama = pendingWork.SonSonuc;

        var sonTurHam = await settings.GetStringAsync(SettingsStore.AutoSendLastRunUtcKey, "", ct);
        DateTime? sonTurYerel = DateTime.TryParse(sonTurHam,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var st)
            ? AzTime.ToLocal(st)
            : null;
        var sonTurOzet = await settings.GetStringAsync(SettingsStore.AutoSendLastRunOzetKey, "", ct);

        return new GunSonuRaporu(
            gun, ortam, otomatikAcik,
            protokolDurumlari.Count, basariliProtokol, hataliProtokol, eksikProtokol,
            gonderimler.Count, basarili, hatali, atlanan,
            silmeler.Count(s => s.Status == SyncStatus.Success),
            kaynakKirilimi, hataGruplari,
            hataliHepsi.Take(HataliKalemTavani).ToList(), hataliHepsi.Count,
            sonTarama?.ToplamProtokolSayisi ?? 0,
            sonTarama?.ToplamTakilanProtokolSayisi ?? 0,
            sonTurYerel,
            string.IsNullOrWhiteSpace(sonTurOzet) ? null : sonTurOzet);
    }
}
