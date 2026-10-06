using Microsoft.Data.SqlClient;

namespace PusulaEHealthSync.Db;

// GONDERILEN KAYDIN ADI (2026-10-06, kullanici istegi).
//
// "bu listede gonderilen veriyi de yazsin: turu doktor ise doktor brans ve adi, islem
// veya tahlil/tetkik ise adlari yazsin, gonderim icerigi alaninda."
//
// SyncLog yalnizca (ResourceType, PusulaId) tutuyor -- "Tetkik 7612934" satiri teknik
// olarak dogru ama okuyana hicbir sey anlatmiyor. Insan okunur ad Pusula'da, bu yuzden
// id'ler toplu halde geri soruluyor (ayni yontem ProtokolIdleriniCozAsync'te de var).
//
// SALT OKUNUR: SELECT'ten baska bir sey yok (projenin 1 numarali kurali).
public partial class PusulaRepository
{
    // {0} id parametre listesi. Sorgular (Id, Aciklama) dondurur.
    //
    // KAYNAKLAR TARAMA SORGULARIYLA AYNI: ekranda gorunen ad ile gonderilen kaydin adi
    // ayrisirsa kullanici hangisine guvenecegini bilemez. Her satirin yanindaki yorum
    // hangi tarama sorgusuyla eslestigini soyluyor.
    private static string? AciklamaSorgusu(string resourceType) => resourceType switch
    {
        // Doktor: ad soyad + bolum. Bolum okunabilir brans -- OrclBrans sayisal kod
        // ("1300"), Ortak.Bolum.Adi ise "Nevrologiya" (olculdu).
        "Practitioner" => @"
            SELECT p.Id,
                   LTRIM(RTRIM(ISNULL(p.Adi,'') + ' ' + ISNULL(p.Soyadi,'')))
                 + CASE WHEN b.Adi IS NULL OR LTRIM(RTRIM(b.Adi)) = '' THEN ''
                        ELSE ' — ' + b.Adi END
            FROM IK.Personel p
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.Id IN ({0})",

        // Islem -- GetCreatedProceduresAsync ile ayni kaynak (oh.Adi)
        "Procedure" => @"
            SELECT pi.Id, oh.Adi
            FROM Hasta.ProtokolIslem pi
            INNER JOIN Ortak.Hizmet oh ON oh.Id = pi.HizmetId
            WHERE pi.Id IN ({0})",

        // Laboratuvar -- GetCompletedLabResultsAsync ile ayni kaynak (TetkikAdi)
        "Observation" => @"
            SELECT lab.LabaratuarSonucId, lab.TetkikAdi
            FROM LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId lab
            WHERE lab.LabaratuarSonucId IN ({0})",

        // Radyoloji -- GetCompletedRadiologyAsync ile ayni kaynak; ad ProtokolIslem
        // uzerinden geliyor cunku RIS.TetkikIslem'de hizmet adi yok.
        "DiagnosticReport" => @"
            SELECT rti.Id, oh.Adi
            FROM RIS.TetkikIslem rti
            INNER JOIN Hasta.ProtokolIslem pi ON pi.Id = rti.ProtokolIslemId
            INNER JOIN Ortak.Hizmet oh ON oh.Id = pi.HizmetId
            WHERE rti.Id IN ({0})",

        // Tani -- GetTanilarByProtokolIdAsync ile ayni kaynak, kod + ad birlikte
        "Condition" => @"
            SELECT pi.Id, LTRIM(RTRIM(ISNULL(ic.Kodu,'') + ' ' + ISNULL(ic.Adi,'')))
            FROM Tedavi.ProtokolICD pi
            INNER JOIN Sube.Tedavi_ICD ic ON ic.Id = pi.ICDId
            WHERE pi.Id IN ({0})",

        // Patoloji raporu ve belgesi ayni tabloya bakiyor (ikisinin de PusulaId'si
        // Result.Id) -- GetCompletedPathologyAsync ile ayni kaynak (Morphology).
        "DiagnosticReport-Patoloji" or "Composition-Patoloji" => @"
            SELECT r.Id, r.Morphology
            FROM [EMR.Pathology].[Result] r
            WHERE r.Id IN ({0})",

        // Patoloji bulgusu -- PusulaId = EPulse.IslemReferansNumarasi
        "Observation-Patoloji" => @"
            SELECT e.IslemReferansNumarasi, MIN(e.MorfolojiKoduValue)
            FROM [EMR.Pathology].[EPulse] e
            WHERE e.IslemReferansNumarasi IN ({0})
            GROUP BY e.IslemReferansNumarasi",

        // Hasta: ad zaten SyncLogEntry.PatientFullName'de tasiniyor, sorgu gereksiz.
        // Muayine/Epikriz: protokol duzeyinde, "Tur" rozeti zaten soyluyor.
        _ => null,
    };

    public static bool AciklamaDestekleniyorMu(string resourceType) => AciklamaSorgusu(resourceType) is not null;

    // PusulaId -> insan okunur ad. Cozulemeyenler sonucta YER ALMAZ; cagiran taraf
    // onlar icin bir sey gostermez (bos satir, uydurma metin degil).
    public async Task<Dictionary<int, string>> KayitAciklamalariAsync(
        string resourceType, IReadOnlyCollection<int> idler, CancellationToken ct = default)
    {
        var sonuc = new Dictionary<int, string>();
        if (idler.Count == 0) return sonuc;
        if (AciklamaSorgusu(resourceType) is not { } sablon) return sonuc;

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);

        // SQL Server'in 2100 parametre siniri -- mevcut kodla ayni payla.
        foreach (var chunk in idler.Distinct().Chunk(1000))
        {
            var isimler = string.Join(",", chunk.Select((_, i) => "@p" + i));
            await using var cmd = new SqlCommand(string.Format(sablon, isimler), conn) { CommandTimeout = 120 };
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@p" + i, chunk[i]);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var ad = reader.GetString(1).Trim();
                if (ad.Length > 0) sonuc[reader.GetInt32(0)] = ad;
            }
        }

        return sonuc;
    }
}
