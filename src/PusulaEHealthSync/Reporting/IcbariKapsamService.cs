using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Reporting;

// ============ ICBARI SIGORTA GONDERIM KAPSAMI (2026-10-26) ============
//
// KULLANICI: "icbari barini hasta listesi olarak degil daha cok rakamlara odaklanalim ve
// tiklayinca filtre edilmis sayfaya gidelim. buradaki istatistigi arttiralim -- bu onemli,
// asil para kaynagi, kazanc kapisi burasi."
//
// TEK HESAP, IKI EKRAN: Genel Bakis istatistigi (IcbariOzeti) ve Icbari Islemler sayfasi
// (Kalemler) ayni listeden turuyor. Ayri hesaplansalardi kartta "312 hatali" yazip
// tiklayinca 309 satir gelebilirdi -- bu projede o tur ayrismanin bedeli birkac kez odendi.
//
// OLCU ISLEM: faturalama kalemi islemin kendisi (Hasta.ProtokolIslem), protokol degil.
// Kume PusulaRepository.GetIcbariIslemlerAsync -- gonderimle BIREBIR ayni eslesme kurallari.
//
// SALT OKUNUR: Pusula'ya yalnizca SELECT.
public class IcbariKapsamService(PusulaRepository repository, SyncLogStore syncLog)
{
    public enum Durum { Gitti, Eksik, Hatali, Denenmedi }

    public record Kalem(IcbariIslemRecord Islem, Durum Durum, string Sebep, string? Mesaj, DateTime? SonDenemeUtc);

    public record SebepSatiri(string Sebep, Durum Durum, int Islem);
    public record HizmetSatiri(string Hizmet, int Toplam, int Gitti, int Eksik, int Hatali, int Denenmedi)
    {
        public int Gitmedi => Toplam - Gitti;
        public double GittiYuzde => Toplam == 0 ? 0 : Math.Round(Gitti * 100.0 / Toplam, 1);
    }

    public record IcbariOzeti(
        int Toplam, int Gitti, int Eksik, int Hatali, int Denenmedi,
        int Protokol, int ProtokolTam,
        List<SebepSatiri> Sebepler, List<HizmetSatiri> Hizmetler, int HizmetCesidi)
    {
        public static readonly IcbariOzeti Bos = new(0, 0, 0, 0, 0, 0, 0, [], [], 0);
        public int Gitmedi => Toplam - Gitti;
        public double GittiYuzde => Toplam == 0 ? 0 : Math.Round(Gitti * 100.0 / Toplam, 1);
        public double Yuzde(int n) => Toplam == 0 ? 0 : Math.Round(n * 100.0 / Toplam, 1);
    }

    public const string HenuzGonderilmedi = "Henüz gönderilmedi";

    // fromLocal/toLocalExclusive: protokolun Pusula'daki ACILIS tarihi (yerel saat).
    public async Task<List<Kalem>> KalemlerAsync(
        DateTime fromLocal, DateTime toLocalExclusive, string ortam, CancellationToken ct = default)
    {
        var islemler = await repository.GetIcbariIslemlerAsync(fromLocal, toLocalExclusive, ct);
        var durumlar = await syncLog.GetLatestByPusulaIdsAsync(
            "Procedure", islemler.Select(i => i.IslemId).ToList(), ct, govdeleriGetir: false, ortamZorla: ortam);

        return islemler.Select(i =>
        {
            var son = durumlar.GetValueOrDefault(i.IslemId);
            // Silinmis (Delete) son kayit "gitti" sayilmaz -- TRƏS'te artik yok.
            var durum = son switch
            {
                null => Durum.Denenmedi,
                { Status: SyncStatus.Success, Operation: SyncOperation.Delete } => Durum.Denenmedi,
                { Status: SyncStatus.Success } => Durum.Gitti,
                { Status: SyncStatus.Skipped } => Durum.Eksik,
                _ => Durum.Hatali,
            };
            var sebep = durum switch
            {
                Durum.Gitti => "Gönderildi",
                Durum.Denenmedi => HenuzGonderilmedi,
                _ => SyncLogEntry.ErrorCategory(son!.Message).Label,
            };
            return new Kalem(i, durum, sebep, durum is Durum.Eksik or Durum.Hatali ? son!.Message : null,
                son?.CreatedAtUtc);
        }).ToList();
    }

    public static IcbariOzeti Ozetle(List<Kalem> kalemler, int hizmetTavan = 10)
    {
        if (kalemler.Count == 0) return IcbariOzeti.Bos;

        var protokoller = kalemler.GroupBy(k => k.Islem.ProtokolId).ToList();
        var hizmetler = kalemler
            .GroupBy(k => HizmetAdi(k.Islem))
            .Select(g => new HizmetSatiri(g.Key, g.Count(),
                g.Count(k => k.Durum == Durum.Gitti), g.Count(k => k.Durum == Durum.Eksik),
                g.Count(k => k.Durum == Durum.Hatali), g.Count(k => k.Durum == Durum.Denenmedi)))
            .ToList();

        return new IcbariOzeti(
            kalemler.Count,
            kalemler.Count(k => k.Durum == Durum.Gitti),
            kalemler.Count(k => k.Durum == Durum.Eksik),
            kalemler.Count(k => k.Durum == Durum.Hatali),
            kalemler.Count(k => k.Durum == Durum.Denenmedi),
            protokoller.Count,
            protokoller.Count(g => g.All(k => k.Durum == Durum.Gitti)),
            kalemler.Where(k => k.Durum != Durum.Gitti)
                .GroupBy(k => (k.Sebep, k.Durum))
                .Select(g => new SebepSatiri(g.Key.Sebep, g.Key.Durum, g.Count()))
                .OrderByDescending(s => s.Islem)
                .ToList(),
            // En cok GITMEYEN hizmet once: "neyi duzeltirsem en cok kazandiririm" sorusunun cevabi.
            hizmetler.Where(h => h.Gitmedi > 0).OrderByDescending(h => h.Gitmedi).Take(hizmetTavan).ToList(),
            hizmetler.Count);
    }

    public static string HizmetAdi(IcbariIslemRecord i) =>
        string.IsNullOrWhiteSpace(i.HizmetAdi) ? i.IcbariAdi : i.HizmetAdi!;

    public static string DurumAnahtari(Durum d) => d switch
    {
        Durum.Gitti => "gitti",
        Durum.Eksik => "eksik",
        Durum.Hatali => "hatali",
        _ => "denenmedi",
    };
}
