using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

public record UserAccount(int Id, string Username, string PasswordHash, string Role, bool Active, DateTime CreatedAtUtc);

// Coklu kullanici + rol (yetkilendirme) deposu -- SyncLog ile ayni SQLite dosyasinda,
// ayri bir tablo. KARAR (2026-08-20, kullanici istegi): tek sabit admin hesabi yerine
// gercek bir kullanici yonetim paneli (bkz. Kullanicilar sayfasi). Ilk calistirmada tablo
// bossa, mevcut DashboardAuth (appsettings/user-secrets) hesabi Admin olarak tohumlanir --
// boylece mevcut giris hemen bozulmaz.
public class UserAccountStore
{
    public const string RoleAdmin = "Admin";
    public const string RoleOperator = "Operator";
    public const string RoleViewer = "Viewer";

    private readonly string _connectionString;

    public UserAccountStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL UNIQUE COLLATE NOCASE,
                PasswordHash TEXT NOT NULL,
                Role TEXT NOT NULL,
                Active INTEGER NOT NULL DEFAULT 1,
                CreatedAtUtc TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    public async Task SeedIfEmptyAsync(string username, string passwordHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(passwordHash)) return;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM Users";
        var count = (long)(await countCmd.ExecuteScalarAsync(ct) ?? 0L);
        if (count > 0) return;

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO Users (Username, PasswordHash, Role, Active, CreatedAtUtc)
            VALUES ($username, $hash, $role, 1, $createdAt)";
        insertCmd.Parameters.AddWithValue("$username", username);
        insertCmd.Parameters.AddWithValue("$hash", passwordHash);
        insertCmd.Parameters.AddWithValue("$role", RoleAdmin);
        insertCmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        await insertCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<UserAccount>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Username, PasswordHash, Role, Active, CreatedAtUtc FROM Users ORDER BY Username";

        var result = new List<UserAccount>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Map(reader));
        return result;
    }

    public async Task<UserAccount?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Username, PasswordHash, Role, Active, CreatedAtUtc FROM Users WHERE Username = $username";
        cmd.Parameters.AddWithValue("$username", username);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<bool> CreateAsync(string username, string passwordHash, string role, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO Users (Username, PasswordHash, Role, Active, CreatedAtUtc)
            VALUES ($username, $hash, $role, 1, $createdAt)";
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$hash", passwordHash);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task SetRoleAsync(int id, string role, CancellationToken ct = default)
        => await ExecuteUpdateAsync("UPDATE Users SET Role = $role WHERE Id = $id",
            ("$role", role), ("$id", id), ct);

    public async Task SetActiveAsync(int id, bool active, CancellationToken ct = default)
        => await ExecuteUpdateAsync("UPDATE Users SET Active = $active WHERE Id = $id",
            ("$active", active ? 1 : 0), ("$id", id), ct);

    public async Task SetPasswordAsync(int id, string passwordHash, CancellationToken ct = default)
        => await ExecuteUpdateAsync("UPDATE Users SET PasswordHash = $hash WHERE Id = $id",
            ("$hash", passwordHash), ("$id", id), ct);

    private async Task ExecuteUpdateAsync(string sql, (string, object) p1, (string, object) p2, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(p1.Item1, p1.Item2);
        cmd.Parameters.AddWithValue(p2.Item1, p2.Item2);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static UserAccount Map(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.GetInt32(4) == 1, DateTime.Parse(r.GetString(5)).ToUniversalTime());
}
