using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// SQLite'a yazar/okur. Baslangicta secildi (bkz. konusma) -- kurulum gerektirmeyen tek
// dosya, ileride web dashboard'un da okuyacagi tablo. Ihtiyac buyurse SQL Server'a
// tasinabilir, sema kucuk oldugu icin maliyeti dusuk.
public class SyncLogStore
{
    private readonly string _connectionString;

    public SyncLogStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS SyncLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ResourceType TEXT NOT NULL,
                PusulaId INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Operation TEXT NULL,
                AzResourceId TEXT NULL,
                Message TEXT NULL,
                RequestJson TEXT NULL,
                ResponseJson TEXT NULL,
                PatientFullName TEXT NULL,
                FathersName TEXT NULL,
                BirthDate TEXT NULL,
                Gender TEXT NULL,
                Fin TEXT NULL,
                RecordOpenedAt TEXT NULL,
                CreatedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_SyncLog_PusulaId ON SyncLog(ResourceType, PusulaId);
            CREATE INDEX IF NOT EXISTS IX_SyncLog_Status ON SyncLog(Status);
        ";
        cmd.ExecuteNonQuery();
    }

    public async Task InsertAsync(SyncLogEntry entry, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SyncLog
                (ResourceType, PusulaId, Status, Operation, AzResourceId, Message, RequestJson, ResponseJson,
                 PatientFullName, FathersName, BirthDate, Gender, Fin, RecordOpenedAt, CreatedAtUtc)
            VALUES
                ($resourceType, $pusulaId, $status, $operation, $azId, $message, $request, $response,
                 $fullName, $fathersName, $birthDate, $gender, $fin, $recordOpenedAt, $createdAt);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$resourceType", entry.ResourceType);
        cmd.Parameters.AddWithValue("$pusulaId", entry.PusulaId);
        cmd.Parameters.AddWithValue("$status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("$operation", (object?)entry.Operation?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$azId", (object?)entry.AzResourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$message", (object?)entry.Message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$request", (object?)entry.RequestJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$response", (object?)entry.ResponseJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fullName", (object?)entry.PatientFullName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fathersName", (object?)entry.FathersName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$birthDate", (object?)entry.BirthDate?.ToString("yyyy-MM-dd") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$gender", (object?)entry.Gender ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fin", (object?)entry.Fin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$recordOpenedAt", (object?)entry.RecordOpenedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", entry.CreatedAtUtc.ToString("O"));
        var newId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        entry.Id = newId;
    }

    private const string SelectColumns = @"Id, ResourceType, PusulaId, Status, Operation, AzResourceId, Message,
            RequestJson, ResponseJson, PatientFullName, FathersName, BirthDate, Gender, Fin, RecordOpenedAt, CreatedAtUtc";

    // fromUtc/toUtcExclusive -- KULLANICI ISTEGI (2026-08-25): "aktivite akisinda tarihe
    // gore listeleme olsun". CreatedAtUtc ISO 8601 metin olarak saklaniyor -- bu formatta
    // sozlukbilimsel (string) karsilastirma kronolojik siralamayla AYNI sonucu verir,
    // ayrica bir donusum gerekmiyor.
    public async Task<List<SyncLogEntry>> QueryAsync(
        string? status, string? resourceType, int take, int skip,
        DateTime? fromUtc = null, DateTime? toUtcExclusive = null, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {SelectColumns}
            FROM SyncLog
            WHERE ($status IS NULL OR Status = $status)
              AND ($resourceType IS NULL OR ResourceType = $resourceType)
              AND ($from IS NULL OR CreatedAtUtc >= $from)
              AND ($to IS NULL OR CreatedAtUtc < $to)
            ORDER BY Id DESC
            LIMIT $take OFFSET $skip";
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$resourceType", (object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)fromUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)toUtcExclusive?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$take", take);
        cmd.Parameters.AddWithValue("$skip", skip);

        var result = new List<SyncLogEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadEntry(reader));
        return result;
    }

    public async Task<SyncLogEntry?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM SyncLog WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntry(reader) : null;
    }

    // Protokol Listesi'nde her satirin "Hasta bilgisi" durumunu N+1 sorgu yapmadan
    // gosterebilmek icin -- verilen pusulaId kumesindeki her biri icin EN SON kaydi
    // (Id'ye gore) tek sorguda doner.
    public async Task<Dictionary<int, SyncLogEntry>> GetLatestByPusulaIdsAsync(
        string resourceType, IReadOnlyCollection<int> pusulaIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, SyncLogEntry>();
        if (pusulaIds.Count == 0) return result;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        var placeholders = pusulaIds.Select((_, i) => $"$id{i}").ToList();
        cmd.CommandText = $@"
            SELECT {SelectColumns}
            FROM SyncLog
            WHERE ResourceType = $resourceType
              AND PusulaId IN ({string.Join(",", placeholders)})
              AND Id IN (
                  SELECT MAX(Id) FROM SyncLog
                  WHERE ResourceType = $resourceType AND PusulaId IN ({string.Join(",", placeholders)})
                  GROUP BY PusulaId
              )";
        cmd.Parameters.AddWithValue("$resourceType", resourceType);
        var idList = pusulaIds.ToList();
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"$id{i}", idList[i]);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var entry = ReadEntry(reader);
            result[entry.PusulaId] = entry;
        }
        return result;
    }

    // Protokol silinme mutabakati icin -- su an e-Health'te "canli" (basariyla
    // olusturulmus/guncellenmis, sonradan silinmemis) sayilan her Encounter'in EN SON
    // kaydini doner. Cagiran taraf (Index sayfasi) bunlarin PusulaId'lerini alip Pusula'daki
    // GUNCEL State'i kontrol eder -- State=0 (iptal/silinmis) cikanlar "gonderilmis ama
    // Pusula'da silinmis, e-Health'ten de silinmeli" olarak isaretlenir.
    public async Task<List<SyncLogEntry>> GetActiveSentEncounterEntriesAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT {SelectColumns}
            FROM SyncLog
            WHERE ResourceType = 'Encounter'
              AND AzResourceId IS NOT NULL
              AND (Operation IS NULL OR Operation <> 'Delete')
              AND Id IN (
                  SELECT MAX(Id) FROM SyncLog WHERE ResourceType = 'Encounter' GROUP BY PusulaId
              )";

        var result = new List<SyncLogEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadEntry(reader));
        return result;
    }

    public async Task<Dictionary<string, int>> GetStatusCountsAsync(
        string? resourceType = null, DateTime? fromUtc = null, DateTime? toUtcExclusive = null, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Status, COUNT(*)
            FROM SyncLog
            WHERE ($resourceType IS NULL OR ResourceType = $resourceType)
              AND ($from IS NULL OR CreatedAtUtc >= $from)
              AND ($to IS NULL OR CreatedAtUtc < $to)
            GROUP BY Status";
        cmd.Parameters.AddWithValue("$resourceType", (object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)fromUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)toUtcExclusive?.ToString("O") ?? DBNull.Value);
        var result = new Dictionary<string, int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    private static SyncLogEntry ReadEntry(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ResourceType = reader.GetString(1),
        PusulaId = reader.GetInt32(2),
        Status = Enum.Parse<SyncStatus>(reader.GetString(3)),
        Operation = reader.IsDBNull(4) ? null : Enum.Parse<SyncOperation>(reader.GetString(4)),
        AzResourceId = reader.IsDBNull(5) ? null : reader.GetString(5),
        Message = reader.IsDBNull(6) ? null : reader.GetString(6),
        RequestJson = reader.IsDBNull(7) ? null : reader.GetString(7),
        ResponseJson = reader.IsDBNull(8) ? null : reader.GetString(8),
        PatientFullName = reader.IsDBNull(9) ? null : reader.GetString(9),
        FathersName = reader.IsDBNull(10) ? null : reader.GetString(10),
        BirthDate = reader.IsDBNull(11) ? null : DateOnly.Parse(reader.GetString(11)),
        Gender = reader.IsDBNull(12) ? null : reader.GetString(12),
        Fin = reader.IsDBNull(13) ? null : reader.GetString(13),
        RecordOpenedAt = reader.IsDBNull(14) ? null : DateTime.Parse(reader.GetString(14)),
        CreatedAtUtc = DateTime.Parse(reader.GetString(15)).ToUniversalTime(),
    };
}
