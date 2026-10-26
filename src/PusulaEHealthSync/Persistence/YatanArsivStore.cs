using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// YATAN HASTA DOSYASININ ARSIV IS AKISI (2026-10-26, kullanici istegi: "bu gonderimi takip
// edecek departman arsiv -- arsive al, tamamla fonksiyonlari olmali, hatta arsiv numarasi
// vermeli").
//
// KULLANICI KARARLARI (2026-10-26):
//   - Akis: taburcu -> ARSIVE AL (arsiv numarasi verilir) -> TAMAMLA. TRƏS'e gonderim
//     bunlardan AYRI bir dugme -- tamamlamak gondermek demek degil.
//   - Arsiv numarasi: yil bazli sira OTOMATIK ONERILIR (2026-000123), elle degistirilebilir.
//     Ayni numara iki dosyaya verilemez (UNIQUE).
//
// NEDEN PUSULA'DA DEGIL: Pusula salt okunur (projenin 1 numarali kurali). Arsiv durumu bu
// panelin kendi verisi; synclog.db'de, diger eslestirme tablolarinin yaninda.
//
// HAREKET GUNLUGU: kim, ne zaman, ne yapti -- arsivde "bu dosyayi kim kapatti" sorusu
// mutlaka soruluyor. Geri alinan islemler de silinmiyor, gunlukte kaliyor.
public class YatanArsivStore
{
    private readonly string _connectionString;

    public YatanArsivStore(string dbPath)
    {
        _connectionString = SqliteDb.ConnectionString(dbPath);
        EnsureSchema();
    }

    public record ArsivKaydi(int ProtokolId, string ArsivNo, string ArsiveAlan, DateTime ArsiveAlinmaUtc,
                             string? Tamamlayan, DateTime? TamamlanmaUtc, string? Notu)
    {
        public bool Tamamlandi => TamamlanmaUtc is not null;
    }

    public record Hareket(DateTime Utc, string Kullanici, string Islem, string? Detay);

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS YatanArsiv (
                ProtokolId INTEGER PRIMARY KEY,
                ArsivNo TEXT NOT NULL UNIQUE,
                ArsiveAlan TEXT NOT NULL,
                ArsiveAlinmaUtc TEXT NOT NULL,
                Tamamlayan TEXT NULL,
                TamamlanmaUtc TEXT NULL,
                Notu TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS YatanArsivHareket (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProtokolId INTEGER NOT NULL,
                Utc TEXT NOT NULL,
                Kullanici TEXT NOT NULL,
                Islem TEXT NOT NULL,
                Detay TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_YatanArsivHareket_Protokol ON YatanArsivHareket(ProtokolId, Id);";
        cmd.ExecuteNonQuery();
    }

    public async Task<Dictionary<int, ArsivKaydi>> GetByProtokolIdsAsync(
        IReadOnlyCollection<int> protokolIdleri, CancellationToken ct = default)
    {
        var sonuc = new Dictionary<int, ArsivKaydi>();
        if (protokolIdleri.Count == 0) return sonuc;
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        foreach (var kume in protokolIdleri.Distinct().Chunk(900))
        {
            using var cmd = conn.CreateCommand();
            var adlar = string.Join(",", kume.Select((_, i) => $"$p{i}"));
            for (var i = 0; i < kume.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", kume[i]);
            cmd.CommandText = $@"
                SELECT ProtokolId, ArsivNo, ArsiveAlan, ArsiveAlinmaUtc, Tamamlayan, TamamlanmaUtc, Notu
                FROM YatanArsiv WHERE ProtokolId IN ({adlar})";
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (reader.Read())
            {
                var k = Oku(reader);
                sonuc[k.ProtokolId] = k;
            }
        }
        return sonuc;
    }

    // Arsiv numarasi aramasi -- "2026-000123" yazan arsivci protokolu bulabilsin.
    public async Task<ArsivKaydi?> GetByArsivNoAsync(string arsivNo, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ProtokolId, ArsivNo, ArsiveAlan, ArsiveAlinmaUtc, Tamamlayan, TamamlanmaUtc, Notu
            FROM YatanArsiv WHERE ArsivNo = $no";
        cmd.Parameters.AddWithValue("$no", arsivNo.Trim());
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return reader.Read() ? Oku(reader) : null;
    }

    // SIRADAKI NUMARA ONERISI: o yilin en buyuk sira numarasi + 1. Elle verilmis ve bicime
    // uymayan numaralar (ornegin "RAF-12") siraya karismaz. Oneri yalnizca bir oneri --
    // kaydederken UNIQUE kurali yine gecerli, iki kisi ayni anda ayni numarayi alamaz.
    public async Task<string> SiradakiNoAsync(int yil, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MAX(CAST(substr(ArsivNo, 6) AS INTEGER)) FROM YatanArsiv
            WHERE ArsivNo LIKE $onek AND length(ArsivNo) = 11
              AND substr(ArsivNo, 6) NOT GLOB '*[^0-9]*'";
        cmd.Parameters.AddWithValue("$onek", $"{yil}-%");
        var enBuyuk = await cmd.ExecuteScalarAsync(ct);
        var sira = enBuyuk is long l ? l + 1 : 1;
        return $"{yil}-{sira.ToString("000000", CultureInfo.InvariantCulture)}";
    }

    // Donus: null = basarili, aksi halde kullaniciya gosterilecek hata.
    public async Task<string?> ArsiveAlAsync(int protokolId, string arsivNo, string kullanici, string? notu,
                                             CancellationToken ct = default)
    {
        arsivNo = arsivNo.Trim();
        if (arsivNo.Length is 0 or > 40) return "Arşiv numarası boş olamaz (en fazla 40 karakter).";

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO YatanArsiv (ProtokolId, ArsivNo, ArsiveAlan, ArsiveAlinmaUtc, Notu)
                VALUES ($pid, $no, $kim, $utc, $not)";
            cmd.Parameters.AddWithValue("$pid", protokolId);
            cmd.Parameters.AddWithValue("$no", arsivNo);
            cmd.Parameters.AddWithValue("$kim", kullanici);
            cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$not", (object?)Temiz(notu) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            await HareketYazAsync(conn, tx, protokolId, kullanici, "Arşive alındı", $"Arşiv no: {arsivNo}", ct);
            tx.Commit();
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            tx.Rollback();
            var mevcut = await GetByArsivNoAsync(arsivNo, ct);
            return mevcut is not null && mevcut.ProtokolId != protokolId
                ? $"{arsivNo} numarası protokol {mevcut.ProtokolId} için kullanılıyor."
                : "Bu protokol zaten arşivde.";
        }
    }

    public async Task<string?> NoDegistirAsync(int protokolId, string yeniNo, string kullanici, CancellationToken ct = default)
    {
        yeniNo = yeniNo.Trim();
        if (yeniNo.Length is 0 or > 40) return "Arşiv numarası boş olamaz (en fazla 40 karakter).";
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE YatanArsiv SET ArsivNo = $no WHERE ProtokolId = $pid";
            cmd.Parameters.AddWithValue("$no", yeniNo);
            cmd.Parameters.AddWithValue("$pid", protokolId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) { tx.Rollback(); return "Protokol arşivde değil."; }
            await HareketYazAsync(conn, tx, protokolId, kullanici, "Arşiv no değişti", $"Yeni no: {yeniNo}", ct);
            tx.Commit();
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            tx.Rollback();
            return $"{yeniNo} numarası başka bir dosyada kullanılıyor.";
        }
    }

    public Task<string?> TamamlaAsync(int protokolId, string kullanici, string? notu, CancellationToken ct = default) =>
        DurumGuncelleAsync(protokolId, kullanici,
            "UPDATE YatanArsiv SET Tamamlayan = $kim, TamamlanmaUtc = $utc, Notu = COALESCE($not, Notu) WHERE ProtokolId = $pid AND TamamlanmaUtc IS NULL",
            "Tamamlandı", Temiz(notu), "Protokol arşivde değil ya da zaten tamamlanmış.", ct);

    public Task<string?> TamamlamayiGeriAlAsync(int protokolId, string kullanici, CancellationToken ct = default) =>
        DurumGuncelleAsync(protokolId, kullanici,
            "UPDATE YatanArsiv SET Tamamlayan = NULL, TamamlanmaUtc = NULL WHERE ProtokolId = $pid AND TamamlanmaUtc IS NOT NULL",
            "Tamamlama geri alındı", null, "Dosya tamamlanmamış.", ct);

    // Yalnizca TAMAMLANMAMIS dosya arsivden cikarilir -- tamamlanmis bir dosyanin numarasini
    // serbest birakmak, arsiv defterindeki kaydi sessizce gecersiz kilardi.
    public Task<string?> ArsivdenCikarAsync(int protokolId, string kullanici, CancellationToken ct = default) =>
        DurumGuncelleAsync(protokolId, kullanici,
            "DELETE FROM YatanArsiv WHERE ProtokolId = $pid AND TamamlanmaUtc IS NULL",
            "Arşivden çıkarıldı", null, "Tamamlanmış dosya arşivden çıkarılamaz; önce tamamlamayı geri alın.", ct);

    public async Task<List<Hareket>> HareketlerAsync(int protokolId, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Utc, Kullanici, Islem, Detay FROM YatanArsivHareket WHERE ProtokolId = $pid ORDER BY Id DESC";
        cmd.Parameters.AddWithValue("$pid", protokolId);
        var sonuc = new List<Hareket>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (reader.Read())
            sonuc.Add(new Hareket(Tarih(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                                  reader.IsDBNull(3) ? null : reader.GetString(3)));
        return sonuc;
    }

    // TRƏS gonderimi de dosyanin gecmisine yaziliyor -- arsivci "bu dosyayi kim, ne zaman
    // gonderdi" sorusunu ayni yerde gorsun. Arsiv kaydi olmasa da yazilir.
    public async Task GonderimHareketiYazAsync(int protokolId, string kullanici, string detay, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        await HareketYazAsync(conn, tx, protokolId, kullanici, "TRƏS'e gönderildi", detay, ct);
        tx.Commit();
    }

    private async Task<string?> DurumGuncelleAsync(int protokolId, string kullanici, string sql, string islem,
                                                   string? notu, string bulunamadi, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$pid", protokolId);
        cmd.Parameters.AddWithValue("$kim", kullanici);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$not", (object?)notu ?? DBNull.Value);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) { tx.Rollback(); return bulunamadi; }
        await HareketYazAsync(conn, tx, protokolId, kullanici, islem, notu, ct);
        tx.Commit();
        return null;
    }

    private static async Task HareketYazAsync(SqliteConnection conn, SqliteTransaction tx, int protokolId,
                                              string kullanici, string islem, string? detay, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO YatanArsivHareket (ProtokolId, Utc, Kullanici, Islem, Detay)
            VALUES ($pid, $utc, $kim, $islem, $detay)";
        cmd.Parameters.AddWithValue("$pid", protokolId);
        cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$kim", kullanici);
        cmd.Parameters.AddWithValue("$islem", islem);
        cmd.Parameters.AddWithValue("$detay", (object?)detay ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static ArsivKaydi Oku(SqliteDataReader r) => new(
        r.GetInt32(0), r.GetString(1), r.GetString(2), Tarih(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : Tarih(r.GetString(5)),
        r.IsDBNull(6) ? null : r.GetString(6));

    private static DateTime Tarih(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string? Temiz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
