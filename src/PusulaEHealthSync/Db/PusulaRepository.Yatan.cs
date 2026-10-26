using Microsoft.Data.SqlClient;

namespace PusulaEHealthSync.Db;

// YATAN HASTALAR EKRANI ICIN PROTOKOL LISTESI (2026-10-26, kullanici istegi: "yatan
// hastalarin gonderimlerini manuel yapabilmek ve takip edilmesi icin ayri bir ekran;
// bu gonderimi takip edecek departman arsiv -- arsive al, tamamla, arsiv numarasi").
//
// IKI KUME TEK SORGUDA:
//   1. Aralikta TABURCU olanlar -- arsivin isi taburcuyla basliyor, filtre bu tarihte.
//   2. Su an YATANLAR (taburcu yok) -- tarihten bagimsiz; yalnizca devreye alim
//      tarihinden sonra yatanlar, ondan oncesi zaten kapsam disi.
//
// TABURCU OLCUTU GONDERIMLE AYNI: once Tedavi.Yatis.TaburcuTarihi (klinik), yoksa protokolun
// KapanisTarihi (idari) -- bkz. PendingWorkService.UygunlukAni ve ProtokolListItem.TaburcuTarihi.
// Ayri olcut kullanilsaydi ekranda "taburcu" gorunen protokol gonderimde "yatiyor" sayilirdi.
//
// SALT OKUNUR: yalnizca SELECT (projenin 1 numarali kurali). Olculdu: 30 gun, ~0,8 sn.
public partial class PusulaRepository
{
    public record YatanProtokol(ProtokolListItem Protokol, DateTime? YatisTarihi, DateTime? Taburcu)
    {
        public bool Yatiyor => Taburcu is null;
    }

    public async Task<List<YatanProtokol>> GetYatanProtokollerAsync(
        DateTime taburcuBas, DateTime taburcuBitHaric, DateTime? yatanTaban, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State,
                y.YatisTarihi, y.TaburcuTarihi
            FROM hasta.protokol p
            LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            OUTER APPLY (
                -- Bir protokolde birden cok yatis olabilir (servis degisimi): ilk kabul,
                -- son taburcu.
                SELECT MIN(COALESCE(yt.HastaKabulTarihi, yt.ServisKabulTarihi)) AS YatisTarihi,
                       MAX(yt.TaburcuTarihi) AS TaburcuTarihi
                FROM Tedavi.Yatis yt
                WHERE yt.ProtokolId = p.Id AND yt.State <> 0
            ) y
            WHERE p.State <> 0 AND p.GelisTipiId = 'Y'
              -- Indeksli kolonda alt sinir: bir yillik yatis tavani, taramayi daraltir.
              AND p.AcilisTarihi >= DATEADD(day, -365, @Bas)
              AND (
                    (COALESCE(y.TaburcuTarihi, p.KapanisTarihi) >= @Bas
                     AND COALESCE(y.TaburcuTarihi, p.KapanisTarihi) < @Bit)
                 OR (y.TaburcuTarihi IS NULL AND p.KapanisTarihi IS NULL AND p.AcilisTarihi >= @Taban)
              )
            -- Once yatanlar, sonra en yeni taburcu. (Sabit bir 'cok ileri tarih' KULLANILMIYOR:
            -- TaburcuTarihi smalldatetime, ust siniri 2079 -- '9999-12-31' donusumde patliyor.)
            ORDER BY CASE WHEN COALESCE(y.TaburcuTarihi, p.KapanisTarihi) IS NULL THEN 0 ELSE 1 END,
                     COALESCE(y.TaburcuTarihi, p.KapanisTarihi) DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@Bas", taburcuBas);
        cmd.Parameters.AddWithValue("@Bit", taburcuBitHaric);
        // Taban yoksa su an yatanlarin hepsi -- yine de bir yil geriye bakan alt sinir gecerli.
        cmd.Parameters.AddWithValue("@Taban", yatanTaban ?? DateTime.MinValue.AddYears(1800));

        var sonuc = new List<YatanProtokol>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var p = MapProtokol(reader);
            p.TaburcuTarihi = GetDateTime(reader, "TaburcuTarihi");
            sonuc.Add(new YatanProtokol(p,
                GetDateTime(reader, "YatisTarihi") ?? p.AcilisTarihi,
                p.TaburcuTarihi ?? p.KapanisTarihi));
        }
        return sonuc;
    }
}
