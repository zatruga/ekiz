using Microsoft.Data.SqlClient;

namespace PusulaEHealthSync.Db;

// GONDERILEMEYEN LABORATUVAR TETKIKLERI RAPORU (2026-10-08, kullanici istegi).
//
// NEDEN: sunucu gunlugunde olculdu -- atlanan laboratuvar kayitlarinin %70'i (21.076
// kayit) "İcbari Sigorta Fiyat Listesi eşleşmesi bulunamadı" diyordu. Bakanlik profili
// Observation.extension:procedure-code alanini ZORUNLU tutuyor ve degeri Icbari hizmet
// kodundan geliyor; o kod yoksa kaynak uretilemiyor bile -- gonderilip reddedilmiyor,
// hic olusturulamiyor.
//
// Kod tarafinda yapilacak bir sey YOK; eksik olan Pusula'daki tanim. Bu rapor, tanimi
// tamamlayacak kisiye "once hangisini duzeltirsem en cok kazandiririm" sorusunun
// cevabini veriyor.
//
// SALT OKUNUR: SELECT'ten baska bir sey yok (projenin 1 numarali kurali).
public partial class PusulaRepository
{
    public record EksikEslestirmeSatiri(
        string TetkikAdi, int SonucSayisi, int ProtokolSayisi,
        string LoincKodu, string HizmetAdi, string Sebep);

    // ZINCIR LabResultObservationMapper'in kullandigiyla BIREBIR AYNI (bkz.
    // GetLabResultsByProtokolIdAsync): LoincKodu -> LIS.Test -> HizmetId -> Ortak.Hizmet
    // -> HizmetKurumHizmet -> KurumHizmet (KurumHizmetKategoriId = 13 = Icbari).
    // Farkli olsaydi rapor "eksik" dedigi halde gonderim calisir, ya da tersi olurdu.
    //
    // ONCE TEKLE, SONRA COZ: ilk yazimda zincir HER SONUC SATIRI icin cozuluyordu
    // (dort OUTER APPLY x ~100.000 satir) ve sorgu 110 saniye suruyordu. Oysa zincir
    // yalnizca LOINC koduna bagli -- ayni kod hep ayni sonucu verir. Kodlar once
    // teklenip sayiliyor (birkac yuz satir), zincir ondan sonra cozuluyor: 110 sn -> 14 sn.
    public async Task<List<EksikEslestirmeSatiri>> GonderilemeyenLabTetkikleriAsync(
        DateTime fromLocal, DateTime toLocalExclusive, CancellationToken ct = default)
    {
        const string sql = @"
            WITH Sonuc AS (
                SELECT lab.LoincKodu,
                       MIN(lab.TetkikAdi)          AS TetkikAdi,
                       COUNT(*)                    AS SonucSayisi,
                       COUNT(DISTINCT lab.VisitId) AS ProtokolSayisi
                FROM LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId lab
                WHERE lab.Status = 6
                  AND lab.TetkikSonucOnayTarihi >= @From AND lab.TetkikSonucOnayTarihi < @To
                GROUP BY lab.LoincKodu
            )
            SELECT
                s.TetkikAdi, s.SonucSayisi, s.ProtokolSayisi,
                ISNULL(s.LoincKodu, '') AS LoincKodu,
                ISNULL(oh.Adi, '')      AS HizmetAdi,
                CASE
                    WHEN s.LoincKodu IS NULL OR s.LoincKodu = '' THEN 'LOINC kodu yok'
                    WHEN t.TestId IS NULL                        THEN 'LIS.Test karşılığı yok'
                    WHEN t.HizmetId IS NULL                      THEN 'Test bir hizmete bağlı değil'
                    WHEN oh.Id IS NULL                           THEN 'Hizmet tanımı bulunamadı'
                    ELSE 'Hizmetin İcbari karşılığı yok'
                END AS Sebep
            FROM Sonuc s
            OUTER APPLY (
                SELECT TOP 1 t.Id AS TestId, t.HizmetId
                FROM LIS.Test t
                WHERE t.LoincKodu = s.LoincKodu
                ORDER BY CASE WHEN t.State <> 0 THEN 0 ELSE 1 END, t.Id DESC
            ) t
            LEFT JOIN Ortak.Hizmet oh ON oh.Id = t.HizmetId
            OUTER APPLY (
                SELECT TOP 1 PKH.Kodu FROM Ortak.HizmetKurumHizmet OHKH
                INNER JOIN Pazarlama.KurumHizmet PKH ON PKH.Id = OHKH.KurumHizmetId
                WHERE OHKH.HizmetId = oh.Id AND PKH.KurumHizmetKategoriId = 13
                  AND PKH.State <> 0 AND OHKH.State <> 0
                ORDER BY PKH.IsPaket DESC
            ) icb
            OUTER APPLY (
                SELECT TOP 1 parentTest.HizmetId AS PanelHizmetId
                FROM LIS.TestParametre tp
                INNER JOIN LIS.Test parentTest ON parentTest.Id = tp.TestId
                WHERE tp.AltTestId = t.TestId AND tp.State <> 0
                ORDER BY tp.Sira
            ) panel
            OUTER APPLY (
                SELECT TOP 1 PKH2.Kodu FROM Ortak.HizmetKurumHizmet OHKH2
                INNER JOIN Pazarlama.KurumHizmet PKH2 ON PKH2.Id = OHKH2.KurumHizmetId
                WHERE OHKH2.HizmetId = panel.PanelHizmetId AND PKH2.KurumHizmetKategoriId = 13
                  AND PKH2.State <> 0 AND OHKH2.State <> 0
                ORDER BY PKH2.IsPaket DESC
            ) panelIcb
            -- PANELIN KODU DA YEDEK: alt parametrenin kendi Icbari kodu yoksa gonderim
            -- panelin koduna dusuyor (bkz. LabResultRecord.PanelIcbariKodu). Yalnizca
            -- IKISI DE yoksa kayit gercekten gonderilemiyor.
            WHERE icb.Kodu IS NULL AND panelIcb.Kodu IS NULL
            ORDER BY s.SonucSayisi DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@From", fromLocal);
        cmd.Parameters.AddWithValue("@To", toLocalExclusive);

        var sonuc = new List<EksikEslestirmeSatiri>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            sonuc.Add(new EksikEslestirmeSatiri(
                reader.IsDBNull(0) ? "(adsız)" : reader.GetString(0),
                reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return sonuc;
    }
}
