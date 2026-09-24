using System.Reflection;
using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// Pusula hizmeti -> AZ az-procedure-codes eslestirmesi.
//
// LabTestLoincStore ile AYNI kalip (Pusula salt okunur, karar bizim veritabanimizda
// durur, gonderim aninda uygulanir) ama SORU FARKLI. Laboratuvarda tek soru vardi:
// "hangi LOINC kodu?". Burada IKI soru var:
//
//   1. Bu hizmet bakanliga GONDERILMELI mi?
//   2. Gonderilecekse hangi kodla?
//
// Ikinci soruya gecmeden once birincisi sorulmali, cunku olculdu (son 365 gun,
// 1.226.632 hizmet istemi): Icbari eslesmesi olmayan 2.017 hizmetin buyuk kismi
// TIBBI ISLEM DEGIL -- yatak ucreti, recete islemi, CD ucreti, gunluk tedavi paketi.
// Bakanligin listesi tibbi islem listesi; bunlarin orada karsiligi OLMAMASI normal.
// "Gonderilmez" isareti olmasaydi kullanici 2.017 kalemin hepsine kod aramaya
// calisirdi.
//
// OLCUM (2026-09-24):
//   Bakanlik listesinde VAR      1.587 hizmet   918.200 istem  %74,9
//   Icbari eslesmesi YOK         2.017 hizmet   308.432 istem  %25,1
//   Icbari var ama bakanlikta yok    0 hizmet         0 istem  %0
//
// Ucuncu satir SIFIR olmasi onemli: Icbari Sigorta Fiyat Listesi zaten bakanligin
// kendi listesi, yani Icbari kodu olan her hizmet tanimi geregi gecerli bir bakanlik
// kodu tasiyor. ICD-10 ve LOINC'taki "kod var ama listede yok" sorunu burada YOK --
// yanlis kod gonderme riski degil, EKSIK gonderim sorunu var.
//
// AD ESLESTIRMESI DENENDI VE CALISMADI: eslesmeyen 2.017 hizmetin adi, bakanligin
// 6.094 adiyla karsilastirildi (Azerbaycan alfabesi normalize, tam + %90 benzerlik).
// 2.017'de 1 tam eslesme cikti. Yani bunlar "adi farkli yazilmis ayni hizmet" degil.
public class HizmetMappingStore
{
    public const string KaynakOneri = "oneri";
    public const string KaynakKullanici = "kullanici";

    private const string ResourceName = "PusulaEHealthSync.Resources.hizmet-eslestirme.tsv";

    private readonly string _connectionString;

    public HizmetMappingStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
        Tohumla();
    }

    public record Satir(
        int HizmetId, string HizmetKodu, string HizmetAdi, string HizmetTipi, int Istem,
        string IcbariKodu, string? AzKod, bool Gonderilmez, string Kaynak, string UpdatedAtUtc)
    {
        /// <summary>Gonderimde kullanilacak kod: elle girilen varsa o, yoksa Pusula'daki Icbari.</summary>
        public string? EtkinKod => !string.IsNullOrWhiteSpace(AzKod) ? AzKod
            : !string.IsNullOrWhiteSpace(IcbariKodu) ? IcbariKodu : null;

        public bool Eslesti => !Gonderilmez && EtkinKod is not null;
        public bool Bekliyor => !Gonderilmez && EtkinKod is null;

        /// <summary>Icbari'den mi geliyor yoksa elle mi girildi -- ekranda ayirt edilsin.</summary>
        public bool IcbaridenGeliyor => string.IsNullOrWhiteSpace(AzKod) && !string.IsNullOrWhiteSpace(IcbariKodu);
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS HizmetMapping (
                HizmetId     INTEGER PRIMARY KEY,
                HizmetKodu   TEXT NOT NULL DEFAULT '',
                HizmetAdi    TEXT NOT NULL DEFAULT '',
                HizmetTipi   TEXT NOT NULL DEFAULT '',
                Istem        INTEGER NOT NULL DEFAULT 0,
                IcbariKodu   TEXT NOT NULL DEFAULT '',
                AzKod        TEXT NULL,
                Gonderilmez  INTEGER NOT NULL DEFAULT 0,
                Kaynak       TEXT NOT NULL DEFAULT 'oneri',
                UpdatedAtUtc TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    // Gomulu liste hizmet KATALOGUDUR (kod, ad, tip, hacim, Icbari kodu). Bunlarin
    // hepsi METADATA -- her acilista tazelenir. AzKod, Gonderilmez ve Kaynak ise
    // KARAR'dir, var olan satirda ASLA degismez (LabTestLoincStore ile ayni ilke).
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
        cmd.CommandText = @"
            INSERT INTO HizmetMapping (HizmetId, HizmetKodu, HizmetAdi, HizmetTipi, Istem, IcbariKodu, AzKod, Gonderilmez, Kaynak, UpdatedAtUtc)
            VALUES ($id, $k, $a, $t, $i, $ic, NULL, 0, $s, $u)
            ON CONFLICT(HizmetId) DO UPDATE SET
                HizmetKodu = excluded.HizmetKodu,
                HizmetAdi  = excluded.HizmetAdi,
                HizmetTipi = excluded.HizmetTipi,
                Istem      = excluded.Istem,
                IcbariKodu = excluded.IcbariKodu;";
        var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pk = cmd.Parameters.Add("$k", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pt = cmd.Parameters.Add("$t", SqliteType.Text);
        var pi = cmd.Parameters.Add("$i", SqliteType.Integer);
        var pic = cmd.Parameters.Add("$ic", SqliteType.Text);
        cmd.Parameters.AddWithValue("$s", KaynakOneri);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));

        while (reader.ReadLine() is { } satir)
        {
            var p = satir.Split('\t');
            if (p.Length < 1 || !int.TryParse(p[0], out var id)) continue;
            pid.Value = id;
            pk.Value = p.Length > 1 ? p[1].Trim() : "";
            pa.Value = p.Length > 2 ? p[2].Trim() : "";
            pt.Value = p.Length > 3 ? p[3].Trim() : "";
            pi.Value = p.Length > 4 && int.TryParse(p[4], out var n) ? n : 0;
            pic.Value = p.Length > 5 ? p[5].Trim() : "";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Gonderimde kullanilan sozluk: HizmetId -> (kod, gonderilmez).
    /// Kod null ve gonderilmez false ise karar henuz verilmemis demektir.
    /// </summary>
    public async Task<Dictionary<int, (string? Kod, bool Gonderilmez)>> GetMapAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT HizmetId, AzKod, IcbariKodu, Gonderilmez FROM HizmetMapping";
        var d = new Dictionary<int, (string?, bool)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var az = r.IsDBNull(1) ? null : r.GetString(1);
            var icb = r.IsDBNull(2) ? "" : r.GetString(2);
            var kod = !string.IsNullOrWhiteSpace(az) ? az : (!string.IsNullOrWhiteSpace(icb) ? icb : null);
            d[r.GetInt32(0)] = (kod, r.GetInt32(3) == 1);
        }
        return d;
    }

    public async Task<List<Satir>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT HizmetId, HizmetKodu, HizmetAdi, HizmetTipi, Istem,
                                   IcbariKodu, AzKod, Gonderilmez, Kaynak, UpdatedAtUtc
                            FROM HizmetMapping ORDER BY Istem DESC, HizmetAdi";
        var list = new List<Satir>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Satir(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7) == 1,
                r.GetString(8), r.GetString(9)));
        return list;
    }

    /// <summary>Elle karar -- kod girmek ya da "gonderilmez" isaretlemek. Kaynak='kullanici' olur.</summary>
    public async Task SetAsync(int hizmetId, string? azKod, bool gonderilmez, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE HizmetMapping
               SET AzKod = $a, Gonderilmez = $g, Kaynak = $s, UpdatedAtUtc = $u
             WHERE HizmetId = $id;";
        cmd.Parameters.AddWithValue("$id", hizmetId);
        cmd.Parameters.AddWithValue("$a", string.IsNullOrWhiteSpace(azKod) ? DBNull.Value : azKod.Trim());
        cmd.Parameters.AddWithValue("$g", gonderilmez ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", KaynakKullanici);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Karari geri al -- satir tohumdaki haline doner (Icbari varsa yine gecerli).</summary>
    public async Task SifirlaAsync(int hizmetId, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE HizmetMapping
                               SET AzKod = NULL, Gonderilmez = 0, Kaynak = $s, UpdatedAtUtc = $u
                             WHERE HizmetId = $id;";
        cmd.Parameters.AddWithValue("$id", hizmetId);
        cmd.Parameters.AddWithValue("$s", KaynakOneri);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
