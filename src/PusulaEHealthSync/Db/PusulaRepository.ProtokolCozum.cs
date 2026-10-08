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

    // ================= TERS YON: PROTOKOL -> COCUK KAYITLAR (2026-10-08) =================
    //
    // KULLANICI ISTEGI: "protokol listesinde hastanin sagindaki gonderilen rozetler --
    // muayene, hasta, epikriz, lab, rad -- bunlar radyoloji ya da laboratuvar gonderilse
    // bile yesil olmuyor."
    //
    // Olculdu: Lab ve Epikriz rozetleri ekranda SABIT metindi (`disabled`, "Henüz
    // gelistirilmedi"), Rad rozeti ise hic yoktu. Yani modüller 2026-08/09'da yazildi ama
    // listedeki gostergeleri hic baglanmadi -- rozetler gonderimi olcmuyordu, hicbir sey
    // olcmuyorlardi.
    //
    // Baglamak icin ters yone ihtiyac var: SyncLog cocuk kaydin id'siyle tutuluyor
    // (Observation -> LabaratuarSonucId), ekran ise protokolden soruyor. Yukaridaki
    // ProtokolIdleriniCozAsync cocuktan protokole gidiyor; bu metot tam tersi.
    //
    // UYGUNLUK KOSULLARI TARAMAYLA AYNI: her sorgu, PendingWork taramasinin o tur icin
    // kullandigi kosulun aynisini tasiyor. Farkli olsaydi rozet "gonderilmedi" derken
    // tarama o kaydi hic aday gormuyor olabilirdi -- kullanici da hic gelmeyecek bir
    // gonderimi beklerdi.
    //
    // SALT OKUNUR: SELECT'ten baska bir sey yok (projenin 1 numarali kurali).
    private static string? CocukSorgusu(string resourceType) => resourceType switch
    {
        // GetCompletedLabResultsAsync ile ayni (Status = 6)
        "Observation" => @"
            SELECT lab.VisitId, lab.LabaratuarSonucId
            FROM LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId lab
            WHERE lab.Status = 6 AND lab.VisitId IN ({0})",

        // GetCompletedRadiologyAsync ile ayni (State = 6). Protokol bagi ProtokolIslem
        // uzerinden -- RIS.TetkikIslem'in kendi ProtokolId'si YOK (olculdu).
        "DiagnosticReport" => @"
            SELECT pi.ProtokolId, rti.Id
            FROM RIS.TetkikIslem rti
            INNER JOIN Hasta.ProtokolIslem pi ON pi.Id = rti.ProtokolIslemId
            WHERE rti.State = 6 AND pi.ProtokolId IN ({0})",

        // GetCompletedPathologyAsync ile ayni (ReportState = 4)
        "DiagnosticReport-Patoloji" => @"
            SELECT r.VisitId, r.Id
            FROM [EMR.Pathology].[Result] r
            WHERE r.ReportState = 4 AND r.VisitId IN ({0})",

        // GetLockedEpikrizAsync ile ayni. PusulaId = ProtokolId oldugu icin "cocuk id"
        // protokolun kendisi; sorgu burada "bu protokolde gonderilecek bir epikriz VAR MI"
        // sorusunu cevapliyor. Bir protokolde birden fazla GenelMuayene satiri olabildigi
        // icin DISTINCT sart -- yoksa tek epikriz iki kez sayilirdi.
        "Composition" => @"
            SELECT DISTINCT g.ProtokolId, g.ProtokolId
            FROM Tedavi.GenelMuayene g
            WHERE g.KilitDurumuId = 1 AND g.State <> 0
              AND g.Epikriz IS NOT NULL AND DATALENGTH(g.Epikriz) > 0
              AND g.ProtokolId IN ({0})",

        _ => null,
    };

    public static bool CocukSorgusuVarMi(string resourceType) => CocukSorgusu(resourceType) is not null;

    // ProtokolId -> o protokolde GONDERILMEYE UYGUN cocuk kayitlarin PusulaId'leri.
    // Hic cocugu olmayan protokol sonucta YER ALMAZ -- cagiran taraf onun icin rozet
    // gostermez ("gonderilmedi" demez, cunku gonderilecek bir sey yok).
    public async Task<Dictionary<int, List<int>>> ProtokolCocukIdleriAsync(
        string resourceType, IReadOnlyCollection<int> protokolIdleri, CancellationToken ct = default)
    {
        var sonuc = new Dictionary<int, List<int>>();
        if (protokolIdleri.Count == 0) return sonuc;
        if (CocukSorgusu(resourceType) is not { } sablon) return sonuc;

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);

        // SQL Server'in 2100 parametre siniri -- mevcut kodla ayni payla.
        foreach (var chunk in protokolIdleri.Distinct().Chunk(1000))
        {
            var isimler = string.Join(",", chunk.Select((_, i) => "@p" + i));
            await using var cmd = new SqlCommand(string.Format(sablon, isimler), conn) { CommandTimeout = 120 };
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@p" + i, chunk[i]);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                var protokolId = reader.GetInt32(0);
                if (!sonuc.TryGetValue(protokolId, out var liste)) sonuc[protokolId] = liste = [];
                liste.Add(reader.GetInt32(1));
            }
        }

        return sonuc;
    }

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
