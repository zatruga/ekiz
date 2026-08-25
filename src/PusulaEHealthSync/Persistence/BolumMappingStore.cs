using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// Pusula Ortak.Bolum -> AZ hospital-departments birebir eslestirmesi, elle girilir
// (bkz. Bolum Eslestirme sayfasi). KARAR (2026-08-20): otomatik isim-bazli eslestirme +
// "Digər"(999) fallback yaklasimi kullanici tarafindan reddedildi -- artik SADECE bu
// tabloda acikca eslestirilmis bir bolum gonderilebiliyor, eslesmeyen SKIPPED oluyor.
public class BolumMappingStore
{
    private readonly string _connectionString;

    public BolumMappingStore(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS BolumMapping (
                PusulaBolumId INTEGER PRIMARY KEY,
                PusulaBolumAdi TEXT NULL,
                AzKod TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    // BolumId -> AZ kodu (null ise henuz eslestirilmemis demek).
    public async Task<Dictionary<int, string?>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PusulaBolumId, AzKod FROM BolumMapping";
        var result = new Dictionary<int, string?>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    public async Task SetAsync(int pusulaBolumId, string? pusulaBolumAdi, string? azKod, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO BolumMapping (PusulaBolumId, PusulaBolumAdi, AzKod, UpdatedAtUtc)
            VALUES ($id, $adi, $azKod, $now)
            ON CONFLICT(PusulaBolumId) DO UPDATE SET
                PusulaBolumAdi = excluded.PusulaBolumAdi,
                AzKod = excluded.AzKod,
                UpdatedAtUtc = excluded.UpdatedAtUtc";
        cmd.Parameters.AddWithValue("$id", pusulaBolumId);
        cmd.Parameters.AddWithValue("$adi", (object?)pusulaBolumAdi ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$azKod", (object?)(string.IsNullOrWhiteSpace(azKod) ? null : azKod) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
