using Microsoft.Data.Sqlite;

const string dbPath = "C:\\Users\\ferhat.ercetin.MLPCARE\\Desktop\\Ekiz\\data\\synclog.db";

await using var conn = new SqliteConnection($"Data Source={dbPath}");
await conn.OpenAsync();
await using var cmd = conn.CreateCommand();
cmd.CommandText = @"
    SELECT Id, ResourceType, PusulaId, Status, Operation, AzResourceId, Message, CreatedAtUtc
    FROM SyncLog
    WHERE (ResourceType = 'Encounter' AND PusulaId = 50729124)
       OR (ResourceType = 'Condition' AND PusulaId IN (SELECT PusulaId FROM SyncLog WHERE ResourceType='Condition' AND Message LIKE 'Z00.0%'))
    ORDER BY Id";
await using var reader = await cmd.ExecuteReaderAsync();
while (await reader.ReadAsync())
{
    Console.WriteLine($"Id={reader.GetInt64(0)} Type={reader.GetString(1)} PusulaId={reader.GetInt32(2)} Status={reader.GetString(3)} " +
        $"Op={(reader.IsDBNull(4) ? "-" : reader.GetString(4))} AzId={(reader.IsDBNull(5) ? "-" : reader.GetString(5))} " +
        $"Msg={(reader.IsDBNull(6) ? "-" : reader.GetString(6))} At={reader.GetString(7)}");
}
