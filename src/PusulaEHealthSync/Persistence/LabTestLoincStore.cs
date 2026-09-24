using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// Pusula'daki lab test kodu -> GERCEK LOINC kodu eslestirmesi.
//
// NEDEN BURADA, PUSULA'DA DEGIL (kullanici sordu 2026-09-23: "bu eslestirmeyi sen
// yapamaz misin?"):
// Pusula veritabani SALT OKUNUR -- LIS.Test.LoincKodu alanina yazamayiz. Ama yazmamiz
// da GEREKMIYOR: eslestirme bizim kendi veritabanimizda durur ve gonderim aninda
// uygulanir. Bolum eslestirmesinde (BolumMappingStore) tam olarak ayni cozum var.
//
// Bu yaklasim Pusula'ya yazmaktan sadece "kural boyle" diye degil, uc somut sebeple
// daha iyi:
//   1. GERI ALINABILIR  -- yanlis kod girilirse kendi tablomuzdan duzeltilir,
//                          hastanenin uretim veritabaninda iz kalmaz.
//   2. KAYNAK BOZULMAZ  -- LIS.Test.LoincKodu hastanenin kendi alani. Oraya bizim
//                          cikarimizi yazmak, laboratuvarin verisi ile bizim
//                          onerimizi ayirt edilemez hale getirirdi. Burada Kaynak
//                          sutunu bunu her zaman gorunur tutuyor.
//   3. SURUM YUKSELTMESINDEN ETKILENMEZ -- HBYS guncellemesi bizim yazdigimizi
//                          silebilir; kendi tablomuz bizde kalir.
//
// ILK CALISTIRMADA TOHUMLAMA: Resources/lab-loinc-oneri.tsv icindeki 61 eslestirme
// Kaynak='oneri' ile yuklenir ve HEMEN kullanilir (kullanici karari 2026-09-23).
// Her biri tx.fhir.org $lookup ile dogrulandi -- yani kodun var oldugu ve ne anlama
// geldigi teyitli; laboratuvar onayi bekleyen sey ESLESTIRMENIN KENDISI.
// Laboratuvar sayfadan degistirdiginde Kaynak='laboratuvar' olur ve sonraki
// tohumlamalar o satira DOKUNMAZ.
public class LabTestLoincStore
{
    public const string KaynakOneri = "oneri";
    public const string KaynakLaboratuvar = "laboratuvar";

    private const string ResourceName = "PusulaEHealthSync.Resources.lab-loinc-oneri.tsv";

    private readonly string _connectionString;

    public LabTestLoincStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
        Tohumla();
    }

    public record Satir(
        string PusulaKodu, string LoincKodu, string? TestAdi, int Istem, string Grup,
        string Kaynak, string UpdatedAtUtc)
    {
        // Grup "belirsiz:1558-6" bicimindeyse taban LOINC kodunu tasir -- laboratuvara
        // "bu kod su LOINC'un varyanti mi?" diye sorabilmek icin.
        public string GrupAdi => Grup.Split(':')[0];
        public string? BelirsizTaban => Grup.StartsWith("belirsiz:") ? Grup[9..] : null;
        public bool Bekliyor => string.IsNullOrWhiteSpace(LoincKodu);
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS LabTestLoincMapping (
                PusulaKodu   TEXT PRIMARY KEY,
                LoincKodu    TEXT NULL,
                TestAdi      TEXT NULL,
                Kaynak       TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();

        // Sonradan eklenen kolonlar -- var olan kurulumlarda tablo zaten olusmus
        // olabilir, o yuzden ALTER ile ekleniyor (SQLite'ta IF NOT EXISTS yok).
        foreach (var ek in new[] { "Istem INTEGER NOT NULL DEFAULT 0", "Grup TEXT NOT NULL DEFAULT 'yok'" })
        {
            try
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE LabTestLoincMapping ADD COLUMN {ek};";
                alter.ExecuteNonQuery();
            }
            catch (SqliteException) { /* kolon zaten var */ }
        }
    }

    // Gomulu oneri listesini yukler. VAR OLAN SATIRA DOKUNMAZ -- laboratuvarin
    // duzelttigi bir eslestirme, uygulama her acildiginda geri alinmamali.
    private void Tohumla()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return;
        using var reader = new StreamReader(stream);

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Istem ve Grup METADATA'dir (kullanim hacmi, neden listede) -- her acilista
        // tazelenir. LoincKodu ve Kaynak KARAR'dir -- var olan satirda ASLA degismez,
        // yoksa laboratuvarin girdigi her sey yeniden baslatmada geri alinirdi.
        cmd.CommandText = @"
            INSERT INTO LabTestLoincMapping (PusulaKodu, LoincKodu, TestAdi, Istem, Grup, Kaynak, UpdatedAtUtc)
            VALUES ($k, $l, $a, $i, $g, $s, $t)
            ON CONFLICT(PusulaKodu) DO UPDATE SET
                Istem = excluded.Istem,
                Grup = excluded.Grup,
                TestAdi = COALESCE(LabTestLoincMapping.TestAdi, excluded.TestAdi);";
        var pk = cmd.Parameters.Add("$k", SqliteType.Text);
        var pl = cmd.Parameters.Add("$l", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
        var pg = cmd.Parameters.Add("$g", SqliteType.Text);
        cmd.Parameters.AddWithValue("$s", KaynakOneri);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));

        while (reader.ReadLine() is { } satir)
        {
            var p = satir.Split('\t');
            if (p.Length < 1 || string.IsNullOrWhiteSpace(p[0])) continue;
            var loinc = p.Length > 1 ? p[1].Trim() : "";
            pk.Value = p[0].Trim();
            pl.Value = string.IsNullOrWhiteSpace(loinc) ? DBNull.Value : loinc;
            pa.Value = p.Length > 2 && !string.IsNullOrWhiteSpace(p[2]) ? p[2].Trim() : (object)DBNull.Value;
            pi.Value = p.Length > 3 && int.TryParse(p[3], out var n) ? n : 0;
            pg.Value = p.Length > 4 && !string.IsNullOrWhiteSpace(p[4]) ? p[4].Trim() : "yok";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Gonderimde kullanilan sozluk: Pusula kodu -> LOINC. Bos eslestirmeler disarida.</summary>
    public async Task<Dictionary<string, string>> GetMapAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PusulaKodu, LoincKodu FROM LabTestLoincMapping WHERE LoincKodu IS NOT NULL AND LoincKodu <> ''";
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public async Task<List<Satir>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // Hacme gore sirali -- laboratuvar nereden baslayacagini gorsun. 247 kaydin
        // cogu yilda 20 kez isteniyor, biri 27.408 kez.
        cmd.CommandText = @"SELECT PusulaKodu, LoincKodu, TestAdi, Istem, Grup, Kaynak, UpdatedAtUtc
                            FROM LabTestLoincMapping ORDER BY Istem DESC, PusulaKodu";
        var list = new List<Satir>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Satir(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3), r.GetString(4),
                r.GetString(5), r.GetString(6)));
        return list;
    }

    /// <summary>Elle duzenleme -- her zaman Kaynak='laboratuvar' isaretler.</summary>
    public async Task SetAsync(string pusulaKodu, string? loincKodu, string? testAdi, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO LabTestLoincMapping (PusulaKodu, LoincKodu, TestAdi, Kaynak, UpdatedAtUtc)
            VALUES ($k, $l, $a, $s, $t)
            ON CONFLICT(PusulaKodu) DO UPDATE SET
                LoincKodu = excluded.LoincKodu,
                TestAdi = COALESCE(excluded.TestAdi, TestAdi),
                Kaynak = excluded.Kaynak,
                UpdatedAtUtc = excluded.UpdatedAtUtc;";
        cmd.Parameters.AddWithValue("$k", pusulaKodu.Trim());
        cmd.Parameters.AddWithValue("$l", string.IsNullOrWhiteSpace(loincKodu) ? DBNull.Value : loincKodu.Trim());
        cmd.Parameters.AddWithValue("$a", (object?)testAdi ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$s", KaynakLaboratuvar);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string pusulaKodu, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LabTestLoincMapping WHERE PusulaKodu = $k";
        cmd.Parameters.AddWithValue("$k", pusulaKodu.Trim());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
