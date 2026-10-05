using Microsoft.Data.SqlClient;

namespace PusulaEHealthSync.Db;

// COCUK KAYIT -> PROTOKOL COZUMU (2026-10-05, gun sonu raporu icin eklendi).
//
// NEDEN GEREKIYOR: SyncLog bir kaydi (ResourceType, PusulaId) ikilisiyle tutuyor ve her
// kaynak tipi KENDI ID UZAYINI kullaniyor -- Observation'in PusulaId'si LabaratuarSonucId,
// Procedure'un ProtokolIslem.Id, Condition'in ProtokolICD.Id. Protokol bagi SyncLog'da
// HIC YOK. Bu bilincli bir tasarim: SyncLog, FHIR kaynak kimliginin kaydi, Pusula
// semasinin aynasi degil.
//
// Gun sonu raporu "x kadar hasta gonderilmesi denendi" demek zorunda (kullanici istegi
// 2026-10-05), yani basarisiz bir laboratuvar satirindan protokole gidebilmemiz lazim.
// Tek yol id'yi Pusula'ya geri sormak.
//
// GECERLILIK KOSULU BILEREK UYGULANMIYOR: TersKontrolKaynagi ayni tablolari
// "t.Status = 6" gibi kosullarla kullaniyor cunku orada soru "bu kayit hala gecerli mi".
// Burada soru farkli -- "bu kayit hangi protokole aitti". Iptal edilmis, onayi kaldirilmis
// bir kaydin protokolunu de bilmek istiyoruz; kosulu uygulasaydik rapor tam da en cok
// ilgilendigimiz satirlari sessizce dusururdu.
//
// SALT OKUNUR: SELECT'ten baska bir sey yok (projenin 1 numarali kurali).
public partial class PusulaRepository
{
    // (Tablo, IdKolonu, ProtokolKolonu). null = bu tip icin cozum yok.
    //
    // Encounter ve Composition listede YOK cunku PusulaId'leri zaten ProtokolId --
    // cagiran taraf onlari sorgusuz gecer (bkz. ProtokolIdleriniCozAsync).
    private static (string Tablo, string IdKolonu, string ProtokolKolonu)? ProtokolCozumKaynagi(
        string resourceType) => resourceType switch
    {
        // Laboratuvar: view'in protokol kolonu VisitId (bkz. GetCompletedLabResultsAsync).
        "Observation" => ("LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId", "LabaratuarSonucId", "VisitId"),

        // Radyoloji BU LISTEDE DEGIL: RIS.TetkikIslem'in ProtokolId kolonu YOK (olculdu --
        // tabloda yalnizca Id, ProtokolDefterNo, ProtokolIslemId var), bu yuzden join
        // gerekiyor ve asagidaki OzelSorgu'da duruyor.

        // Patoloji raporu ve patoloji belgesi AYNI tabloya bakiyor: ikisinin de PusulaId'si
        // Result.Id (bkz. PathologyReportSyncService -- etiketler ayri cunku Composition
        // epikrizde de kullaniliyor ve id'ler cakisabilir).
        "DiagnosticReport-Patoloji" => ("[EMR.Pathology].[Result]", "Id", "VisitId"),
        "Composition-Patoloji" => ("[EMR.Pathology].[Result]", "Id", "VisitId"),

        "Procedure" => ("Hasta.ProtokolIslem", "Id", "ProtokolId"),
        "Condition" => ("Tedavi.ProtokolICD", "Id", "ProtokolId"),

        // Vital bulgular: PusulaId = GenelMuayene.Id (laboratuvardan FARKLI anahtar,
        // bkz. VitalSignsSyncService).
        "Observation-Vital" => ("Tedavi.GenelMuayene", "Id", "ProtokolId"),

        // Patient / Practitioner: protokole ait DEGILLER (hasta ve doktor protokoller
        // arasi paylasilir). Cozulemezler, rapor onlari protokolsuz gosterir.
        _ => null,
    };

    // TEK TABLODAN COZULEMEYEN TIPLER -- join gerektirenler. {0} id parametre listesi.
    //
    // Her iki sorgu da ilk yazimda YANLIS varsayimla gecilmisti: radyolojide
    // RIS.TetkikIslem'in kendi ProtokolId'si oldugu sanildi (yok), patolojide ise bulgu
    // satirinin dogrudan protokolu tasidigi. Ikisi de olculup duzeltildi.
    private static string? OzelSorgu(string resourceType) => resourceType switch
    {
        // RADYOLOJI: protokol bagi ProtokolIslemId uzerinden. Olculdu -- 388.288 tetkikin
        // hepsinde bu kolon dolu ve karsiligi var, INNER JOIN kayit dusurmez.
        "DiagnosticReport" => @"
            SELECT rti.Id, pi.ProtokolId
            FROM RIS.TetkikIslem rti
            INNER JOIN Hasta.ProtokolIslem pi ON pi.Id = rti.ProtokolIslemId
            WHERE rti.Id IN ({0})",

        // PATOLOJI BULGUSU: IKI SICRAMA. PusulaId = EPulse.IslemReferansNumarasi, oradan
        // PatolojiIstekId ile Result'a, Result.VisitId protokol (bkz.
        // GetPathologyFindingsByResultIdAsync ve PathologyReportSyncService).
        "Observation-Patoloji" => @"
            SELECT e.IslemReferansNumarasi, r.VisitId
            FROM [EMR.Pathology].[EPulse] e
            INNER JOIN [EMR.Pathology].[Result] r ON r.Id = e.PatolojiIstekId
            WHERE e.IslemReferansNumarasi IN ({0})",

        _ => null,
    };

    // PusulaId -> ProtokolId. Cozulemeyen id'ler sonucta YER ALMAZ -- cagiran taraf
    // onlari "protokol eslesmedi" olarak gosterir, sessizce dusurmez.
    public async Task<Dictionary<int, int>> ProtokolIdleriniCozAsync(
        string resourceType, IReadOnlyCollection<int> pusulaIdleri, CancellationToken ct = default)
    {
        var sonuc = new Dictionary<int, int>();
        if (pusulaIdleri.Count == 0) return sonuc;

        // PusulaId'si zaten ProtokolId olanlar: sorgu yok, kimlik eslemesi.
        if (resourceType is "Encounter" or "Composition")
        {
            foreach (var id in pusulaIdleri.Distinct()) sonuc[id] = id;
            return sonuc;
        }

        // Tek tablodan cozulebilen tipler ProtokolCozumKaynagi'nda, join gerektirenler
        // OzelSorgu'da.
        string tablo = "", idKolonu = "", protokolKolonu = "";
        string? ozelSql = null;

        if (OzelSorgu(resourceType) is { } ozel)
        {
            ozelSql = ozel;
        }
        else if (ProtokolCozumKaynagi(resourceType) is { } kaynak)
        {
            (tablo, idKolonu, protokolKolonu) = kaynak;
        }
        else
        {
            // Cozulemeyen tip (Patient, Practitioner): bos sozluk doner, cagiran taraf
            // kalemleri protokolsuz gosterir.
            return sonuc;
        }

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);

        // SQL Server'in 2100 parametre siniri -- mevcut kodla ayni payla (bkz.
        // GetExistingIdsAsync).
        foreach (var chunk in pusulaIdleri.Distinct().Chunk(1000))
        {
            var isimler = string.Join(",", chunk.Select((_, i) => "@p" + i));
            var sql = ozelSql is not null
                ? string.Format(ozelSql, isimler)
                : $@"
                    SELECT DISTINCT t.{idKolonu}, t.{protokolKolonu}
                    FROM {tablo} t
                    WHERE t.{idKolonu} IN ({isimler})
                      AND t.{protokolKolonu} IS NOT NULL";

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@p" + i, chunk[i]);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                sonuc[reader.GetInt32(0)] = reader.GetInt32(1);
            }
        }

        return sonuc;
    }
}
