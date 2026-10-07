using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

public record DailyTrendPoint(DateOnly Day, int Success, int Failed);

// SQLite'a yazar/okur. Baslangicta secildi (bkz. konusma) -- kurulum gerektirmeyen tek
// dosya, ileride web dashboard'un da okuyacagi tablo. Ihtiyac buyurse SQL Server'a
// tasinabilir, sema kucuk oldugu icin maliyeti dusuk.
// ORTAM AYRIMI (2026-10-05, kullanici sordu: "canlıda devreye aldığımızda tekrar
// gönderebilecek miyiz? canlıdaki id ile sandboxtaki id farklı, çakışma olacak mı?").
//
// SORUN: SyncLog, bir denemenin HANGI ORTAMA gittigini hic kaydetmiyordu. Sonuclari,
// sandbox'tan canliya gecildigi anda:
//   1. HICBIR SEY GONDERILMEZDI. Sandbox'a basariyla gitmis her kayit "Success" oldugu
//      icin tarama onu "tamam" sayar; canliya gecince de ayni kayitlara bakip "yapacak is
//      yok" derdi. 125.694 basarili sandbox gonderimi, canli ortami bos birakirdi.
//   2. Ekran yalan soylerdi: Protokol Detay'daki yesil "Gönderildi" rozetleri yalnizca
//      sandbox'ta var olan kayitlari gosterirdi.
//   3. SILME YANLIS HEDEFE GIDERDI: saklanan AzResourceId sandbox sunucusunun verdigi id.
//      Canlida o id yok; silme/guncelleme 404 alirdi.
// Id'ler CAKISMAZ (iki ayri sunucu, iki ayri id uzayi) ama saklanan id, yanlis sunucuyu
// isaret eden gecersiz bir isaretciye donusurdu.
//
// COZUM: her kayit hangi ortama gittigini tasiyor ve "gonderilmis mi" sorusunu soran her
// sorgu KENDILIGINDEN o ortama kisitlaniyor.
//
// NEDEN FILTRE BURADA, CAGRI YERLERINDE DEGIL: 69 cagri yeri var (11 dosya). Her birine
// elle "AND Ortam = ..." eklemek, bir tanesinin unutulmasi demekti -- ve unutulan yer
// sessizce yanlis cevap verirdi. Store kendi kendini kisitlayinca unutulacak bir yer
// kalmiyor.
public class SyncLogStore
{
    private readonly string _connectionString;
    private readonly SettingsStore? _settings;

    public SyncLogStore(string dbPath, SettingsStore? settings = null)
    {
        _connectionString = SqliteDb.ConnectionString(dbPath);
        _settings = settings;
        EnsureSchema();
    }

    // Su an hangi ortamdayiz? settings verilmemisse (kucuk arac/test kullanimlari) Test.
    private async Task<string> OrtamAsync(CancellationToken ct)
        => _settings is null
            ? SettingsStore.EHealthEnvironmentDefault
            : await _settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);

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
            -- Aktivite sayfasi ve Genel Bakis grafigi tarih araligiyla filtreliyor
            -- (QueryAsync, GetDailyTrendAsync, GetStatusCountsAsync). Bu indeks olmadan
            -- her tarih filtresi tablonun tamamini tariyordu (2026-09-28 inceleme).
            CREATE INDEX IF NOT EXISTS IX_SyncLog_CreatedAtUtc ON SyncLog(CreatedAtUtc);
        ";
        cmd.ExecuteNonQuery();

        // ORTAM SUTUNU -- sonradan eklendi (2026-10-05), mevcut kurulumlarda ALTER gerekiyor.
        // Var olan satirlarin tamami sandbox'a gitmisti: bugune kadar canli ortam hic
        // kullanilmadi, dolayisiyla geriye donuk doldurma "Test" olarak guvenli.
        using var sutunCmd = conn.CreateCommand();
        sutunCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SyncLog') WHERE name = 'Ortam'";
        if (Convert.ToInt64(sutunCmd.ExecuteScalar()) == 0)
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = @"
                ALTER TABLE SyncLog ADD COLUMN Ortam TEXT NULL;
                UPDATE SyncLog SET Ortam = 'Test' WHERE Ortam IS NULL;
                CREATE INDEX IF NOT EXISTS IX_SyncLog_Ortam ON SyncLog(Ortam, ResourceType, PusulaId);";
            alterCmd.ExecuteNonQuery();
        }
    }

    public async Task InsertAsync(SyncLogEntry entry, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO SyncLog
                (ResourceType, PusulaId, Status, Operation, AzResourceId, Message, RequestJson, ResponseJson,
                 PatientFullName, FathersName, BirthDate, Gender, Fin, RecordOpenedAt, CreatedAtUtc, Ortam)
            VALUES
                ($resourceType, $pusulaId, $status, $operation, $azId, $message, $request, $response,
                 $fullName, $fathersName, $birthDate, $gender, $fin, $recordOpenedAt, $createdAt, $ortam);
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
        // Kayit, O ANKI ortam damgasiyla yaziliyor. Cagiran taraflarin bunu dusunmesi
        // gerekmiyor -- 60'tan fazla cagri yeri var, biri unutulsaydi o kayit ortamsiz
        // kalir ve hicbir sorguya takilmazdi.
        entry.Ortam ??= await OrtamAsync(ct);
        cmd.Parameters.AddWithValue("$ortam", entry.Ortam);
        var newId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        entry.Id = newId;
    }

    private const string SelectColumns = @"Id, ResourceType, PusulaId, Status, Operation, AzResourceId, Message,
            RequestJson, ResponseJson, PatientFullName, FathersName, BirthDate, Gender, Fin, RecordOpenedAt, CreatedAtUtc, Ortam";

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

    // ======================= AKTIVITE AKISI: IKI ASAMALI OKUMA =======================
    //
    // KULLANICI ISTEGI (2026-10-07): "bu liste neden 2 hasta gosteriyor, sonraki dedikce
    // yukarıdaki sayilar degisiyor; hasta sayilari tam gelsin, sayfa listesindeki
    // hizmetlere gore degil hastayi saysin."
    //
    // SORUN SAYFALAMANIN BIRIMINDEYDI: sayfa 60 SATIR cekiyordu, ekran ise onlari
    // protokole gore grupluyordu. Tek bir yatan hastanin bir gunluk laboratuvari 60
    // satiri tek basina doldurabildigi icin 7.554 denemelik bir gun listede 2 hasta
    // olarak gorunuyordu. Sayfa boyutunu buyutmek cozmez -- birim yanlis.
    //
    // Protokole gore sayfalamanin onsarti: satirlar gelmeden ONCE araligin TAMAMININ
    // protokolu cozulmeli (SyncLog'da protokol bagi yok, Pusula'ya soruluyor). Bu yuzden
    // okuma ikiye bolundu:
    //   1. QueryOzetAsync  -- araligin tamami, satir basina dort alan (gruplama + sayim)
    //   2. GetByIdsAsync   -- yalnizca o sayfaya dusen gruplarin satirlari, tam govdeyle
    //
    // Tek parcada yapilamazdi: QueryAsync SelectColumns ile okur, yani her satirin
    // gonderilen ve donen FHIR govdesini de tasir. Gunde 7.500+ satirda govdeleri
    // bellege almak sayfayi cokertirdi; oysa govde yalnizca ekranda GORUNEN satirlar
    // icin gerekiyor.

    // Gruplama ve sayim icin gereken asgari alanlar.
    public record OzetSatiri(long Id, string ResourceType, int PusulaId, SyncStatus Status, string? Message);

    // ORTAM FILTRESI YOK: QueryAsync ve GetStatusCountsAsync ile ayni bilincli karar
    // (gerekcesi GetGunKayitlariAsync'in basinda) -- bu bir EKRAN sorgusu, olcum degil.
    //
    // DURUM FILTRESI DE YOK, BILEREK (2026-10-07 duzeltmesi). Ozet, ust karttaki hasta
    // sayilarini uretiyor ve o sayilar "en kotu durum kazanir" kuraliyla hesaplaniyor:
    // bir hastanin kaydi hata aldiysa o hasta Hatali sayiliyor. Bu kural ancak hastanin
    // TUM kayitlari elde varken isler. Sorgu Status'e gore suzulseydi, "Sorunsuz" karti
    // tiklandiginda yalnizca basarili satirlar okunur, her hasta kendiliginden sorunsuz
    // gorunur ve kart kendi sayisini yeniden yazardi (kullanici bunu yakaladi: Sorunsuz'a
    // tiklayinca toplam 82'den 71'e dusuyor, Eksik veri'ye tiklayinca Sorunsuz 0 oluyordu).
    // Suzme artik SQL'de degil, ekrana hangi gruplarin alinacagi secilirken yapiliyor.
    //
    // MESAJ YALNIZCA HATALI SATIRLAR ICIN: "Hata Kategorisi" filtresi mesaj metninden
    // turetiliyor (DB kolonu degil), ama kategori zaten sadece Failed satirlar icin
    // anlamli. CASE ile basarili/atlanan satirlarin mesaji hic okunmuyor -- 16.000
    // satirlik bir gunde bu, bellege 48 mesaj tasimakla 16.000 mesaj tasimak arasindaki
    // fark demek.
    //
    // tavan: bellek sigortasi. Asilirsa EN YENI satirlar doner ve cagiran taraf
    // kullaniciyi uyarir -- sessizce eksik sayi gostermek, hic gostermemekten kotu.
    public async Task<List<OzetSatiri>> QueryOzetAsync(
        string? resourceType, DateTime? fromUtc, DateTime? toUtcExclusive,
        int tavan, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ResourceType, PusulaId, Status,
                   CASE WHEN Status = 'Failed' THEN Message ELSE NULL END
            FROM SyncLog
            WHERE ($resourceType IS NULL OR ResourceType = $resourceType)
              AND ($from IS NULL OR CreatedAtUtc >= $from)
              AND ($to IS NULL OR CreatedAtUtc < $to)
            ORDER BY Id DESC
            LIMIT $tavan";
        cmd.Parameters.AddWithValue("$resourceType", (object?)resourceType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$from", (object?)fromUtc?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$to", (object?)toUtcExclusive?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tavan", tavan);

        var result = new List<OzetSatiri>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new OzetSatiri(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                Enum.Parse<SyncStatus>(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return result;
    }

    // Id listesine gore TAM kayitlar (govdeler dahil). Donus Id'ye gore AZALAN, yani
    // akisin en yeniden eskiye sirasi korunuyor -- cagiran taraf yeniden siralamak
    // zorunda kalmasin diye (parti parti okundugu icin sira kendiliginden gelmiyor).
    public async Task<List<SyncLogEntry>> GetByIdsAsync(
        IReadOnlyCollection<long> idler, CancellationToken ct = default)
    {
        var result = new List<SyncLogEntry>();
        if (idler.Count == 0) return result;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // SQLite'in 999 degisken siniri -- mevcut kodla ayni parti boyutu.
        foreach (var chunk in idler.Distinct().Chunk(900))
        {
            using var cmd = conn.CreateCommand();
            var placeholders = chunk.Select((_, i) => $"$id{i}").ToList();
            cmd.CommandText = $"SELECT {SelectColumns} FROM SyncLog WHERE Id IN ({string.Join(",", placeholders)})";
            for (var i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(ReadEntry(reader));
        }

        result.Sort((a, b) => b.Id.CompareTo(a.Id));
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
    // Ayni kolon sirasi, ama iki agir kolon (gonderilen/donen FHIR govdesi) yerine NULL.
    // Boylece ReadEntry'nin indeksleri degismeden ayni kod iki sorguyu da okuyabiliyor.
    private const string SelectColumnsGovdesiz = @"Id, ResourceType, PusulaId, Status, Operation, AzResourceId, Message,
            NULL, NULL, PatientFullName, FathersName, BirthDate, Gender, Fin, RecordOpenedAt, CreatedAtUtc, Ortam";

    // govdeleriGetir=false: RequestJson/ResponseJson okunmaz (null gelir). Yalnizca DURUM
    // bilgisine bakan, buyuk id listeleriyle calisan cagiranlar icindir -- bkz.
    // PendingWorkService. Detay ekrani gibi govdeyi GOSTEREN cagiranlar varsayilani
    // (true) kullanmali.
    public async Task<Dictionary<int, SyncLogEntry>> GetLatestByPusulaIdsAsync(
        string resourceType, IReadOnlyCollection<int> pusulaIds, CancellationToken ct = default,
        bool govdeleriGetir = true)
    {
        var ortam = await OrtamAsync(ct);
        var result = new Dictionary<int, SyncLogEntry>();
        if (pusulaIds.Count == 0) return result;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // TEK GECISLI SORGU (2026-09-28 inceleme). Eski hali id listesini sorguda IKI kez
        // kullaniyordu (bir dis WHERE, bir de MAX(Id) alt sorgusunda) -- yani tabloyu iki
        // kez tarayip parametre sayisini de ikiye katliyordu. Bu yuzden parti boyutu
        // SQLite'in 999 degisken sinirina takilmamak icin 400'de tutulmustu.
        //
        // Yeni hali SQLite'in belgelenmis davranisina dayaniyor: sorguda tek aggregate
        // olarak max() kullanildiginda, GROUP BY'daki "ciplak" kolonlar max'in geldigi
        // SATIRIN degerlerini alir. Yani tek gecisle hem en buyuk Id hem o satirin tum
        // kolonlari geliyor.
        //
        // Sonuc: tarama yariya indi, parametre sayisi id sayisina esitlendi, parti boyutu
        // 400'den 900'e cikti (54.000 id icin 135 sorgu yerine 60 sorgu).
        var kolonlar = govdeleriGetir ? SelectColumns : SelectColumnsGovdesiz;
        foreach (var chunk in pusulaIds.Distinct().Chunk(900))
        {
            using var cmd = conn.CreateCommand();
            var placeholders = chunk.Select((_, i) => $"$id{i}").ToList();
            cmd.CommandText = $@"
                SELECT {kolonlar}, MAX(Id)
                FROM SyncLog
                WHERE ResourceType = $resourceType
                  AND Ortam = $ortam
                  AND PusulaId IN ({string.Join(",", placeholders)})
                GROUP BY PusulaId";
            cmd.Parameters.AddWithValue("$resourceType", resourceType);
            cmd.Parameters.AddWithValue("$ortam", ortam);
            for (var i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var entry = ReadEntry(reader);
                result[entry.PusulaId] = entry;
            }
        }
        return result;
    }


    // ARDISIK BASARISIZ DENEME SAYISI -- her PusulaId icin, EN SON BASARILI gonderimden
    // SONRA kac deneme yapildigi.
    //
    // NEDEN GEREKLI (2026-10-03): otomatik gonderim acilmadan once "gecici hata" ile
    // "kalici hata" ayrilmali. Ikisi de SyncLog'da ayni gorunur (Status != Success) ama
    // davranislari zit: ag kesintisi bir sonraki turda kendiliginden duzelir, LOINC kodu
    // olmayan bir tetkik ise HICBIR zaman duzelmez. Ayirt edilmezse ikinci grup her saat
    // yeniden denenir, en eskiden basladigi icin parti kontenjaninin tamamini isgal eder
    // ve hicbir yeni protokol gonderilemez. (Olculdu: 30 gunde 4.289 protokolde boyle bir
    // kalem var -- protokollerin %21'i.)
    //
    // "ARDISIK" onemli: eskiden 3 kez basarisiz olup SONRA basarili olmus bir kayit temiz
    // sayilmali. Bu yuzden toplam basarisiz sayisi degil, son basaridan sonraki sayim
    // aliniyor.
    //
    // CAGRI SEKLI: yalnizca SON DURUMU basarisiz olan id'ler icin cagrilmali (bkz.
    // PendingWorkService). Tarama 54.000 id ile calisiyor; iliskili alt sorguyu hepsi icin
    // calistirmak gereksiz, basarisiz alt kume ise kucuk.
    public async Task<Dictionary<int, int>> GetArdisikBasarisizSayilariAsync(
        string resourceType, IReadOnlyCollection<int> pusulaIds, CancellationToken ct = default)
    {
        var ortam = await OrtamAsync(ct);
        var result = new Dictionary<int, int>();
        if (pusulaIds.Count == 0) return result;

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        foreach (var chunk in pusulaIds.Distinct().Chunk(900))
        {
            using var cmd = conn.CreateCommand();
            var placeholders = chunk.Select((_, i) => $"$id{i}").ToList();
            cmd.CommandText = $@"
                SELECT PusulaId, COUNT(*)
                FROM SyncLog s
                WHERE s.ResourceType = $resourceType
                  AND s.Ortam = $ortam
                  AND s.PusulaId IN ({string.Join(",", placeholders)})
                  AND s.Id > COALESCE((
                        SELECT MAX(b.Id) FROM SyncLog b
                        WHERE b.ResourceType = s.ResourceType
                          AND b.Ortam = s.Ortam
                          AND b.PusulaId = s.PusulaId
                          AND b.Status = 'Success'), 0)
                GROUP BY PusulaId";
            cmd.Parameters.AddWithValue("$resourceType", resourceType);
            cmd.Parameters.AddWithValue("$ortam", ortam);
            for (var i = 0; i < chunk.Length; i++)
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result[reader.GetInt32(0)] = reader.GetInt32(1);
        }
        return result;
    }

    // Protokol silinme mutabakati icin -- su an TRƏS'te "canli" (basariyla
    // olusturulmus/guncellenmis, sonradan silinmemis) sayilan her Encounter'in EN SON
    // kaydini doner. Cagiran taraf (Index sayfasi) bunlarin PusulaId'lerini alip Pusula'daki
    // GUNCEL State'i kontrol eder -- State=0 (iptal/silinmis) cikanlar "gonderilmis ama
    // Pusula'da silinmis, TRƏS'ten de silinmeli" olarak isaretlenir.
    public async Task<List<SyncLogEntry>> GetActiveSentEncounterEntriesAsync(CancellationToken ct = default)
    {
        var ortam = await OrtamAsync(ct);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // Govdesiz projeksiyon (2026-09-28): bu sorgu Ana Sayfa'nin HER acilisinda,
        // bugune kadar basariyla gonderilmis TUM Encounter'lar icin calisiyor. Cagiran
        // (Index sayfasi) yalnizca PusulaId/AzResourceId/hasta bilgisine bakiyor;
        // gonderilen FHIR govdesini hic kullanmiyor. Gunluk buyudukce bu iki kolon
        // sayfa acilisinin en pahali kismi olurdu.
        cmd.CommandText = $@"
            SELECT {SelectColumnsGovdesiz}
            FROM SyncLog
            WHERE ResourceType = 'Encounter'
              AND Ortam = $ortam
              AND AzResourceId IS NOT NULL
              AND (Operation IS NULL OR Operation <> 'Delete')
              AND Id IN (
                  SELECT MAX(Id) FROM SyncLog WHERE ResourceType = 'Encounter' AND Ortam = $ortam GROUP BY PusulaId
              )";

        var result = new List<SyncLogEntry>();
        cmd.Parameters.AddWithValue("$ortam", ortam);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(ReadEntry(reader));
        return result;
    }

    // Bir kaynak tipinde SU AN TRƏS'te CANLI sayilan PusulaId'ler -- yani en son
    // denemesi basarili olan ve sonradan silinmemis olanlar.
    //
    // NEDEN (2026-09-29): laboratuvar iptali Pusula taranarak bulunamiyor (kaynak view
    // iptal edilen satiri hic dondurmuyor). Soru ters cevriliyor: "gonderdiklerim hala
    // duruyor mu?" Bu metot o sorunun SOL tarafini verir.
    public async Task<List<int>> GetLiveSentIdsAsync(string resourceType, CancellationToken ct = default)
    {
        var ortam = await OrtamAsync(ct);
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // Tek gecisli max() kalibi -- GROUP BY'daki ciplak kolonlar en buyuk Id'nin
        // geldigi satirdan gelir (bkz. GetLatestByPusulaIdsAsync'teki ayni aciklama).
        cmd.CommandText = @"
            SELECT PusulaId, Status, Operation, AzResourceId, MAX(Id)
            FROM SyncLog
            WHERE ResourceType = $resourceType
              AND Ortam = $ortam
            GROUP BY PusulaId
            HAVING Status = 'Success'
               AND AzResourceId IS NOT NULL
               AND (Operation IS NULL OR Operation <> 'Delete')";
        cmd.Parameters.AddWithValue("$resourceType", resourceType);
        cmd.Parameters.AddWithValue("$ortam", ortam);

        var result = new List<int>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(reader.GetInt32(0));
        return result;
    }

    // Genel Bakış paneli -- gönderim trendi grafiği icin gunluk basarili/hatali sayilari.
    // DUZELTME (2026-09-09): gun anahtari eskiden dogrudan substr(CreatedAtUtc,1,10) ile
    // aliniyordu -- yani UTC gunune gore. Baki +04:00 oldugu icin YEREL saatle 00:00-04:00
    // arasi yapilan her gonderim BIR ONCEKI gune dusuyordu (gece nobetinde yapilan islem
    // dunun cubugunda gorunuyordu). Artik saklanan UTC degeri once yerele kaydiriliyor
    // (bkz. AzTime), gruplama ondan sonra yapiliyor.
    //
    // Parametreler artik YEREL gun araligi -- cagiran taraf DateTime/UTC karisimi ile
    // ugrasmasin diye UTC'ye cevrim burada yapiliyor (eski imzada "fromUtc" deniyordu ama
    // cagiranlar yerel gece yarisi gonderiyordu; ad ile icerik uyusmuyordu).
    //
    // Aralikta hic kaydi olmayan gunler de 0/0 olarak listede yer alir (grafik bosluksuz
    // cizilsin diye) -- SQL'den sadece VEROLAN gunler doner, eksik gunler burada doldurulur.
    public async Task<List<DailyTrendPoint>> GetDailyTrendAsync(DateOnly fromLocal, DateOnly toLocalInclusive, CancellationToken ct = default)
    {
        var fromUtc = AzTime.ToUtc(fromLocal.ToDateTime(TimeOnly.MinValue));
        var toUtcExclusive = AzTime.ToUtc(toLocalInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue));

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT substr(datetime(CreatedAtUtc, {AzTime.SqliteShiftToLocal}), 1, 10) AS Day, Status, COUNT(*)
            FROM SyncLog
            WHERE CreatedAtUtc >= $from AND CreatedAtUtc < $to
            GROUP BY Day, Status";
        cmd.Parameters.AddWithValue("$from", fromUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$to", toUtcExclusive.ToString("O"));

        var byDay = new Dictionary<string, (int Success, int Failed)>();
        using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var day = reader.GetString(0);
                var status = reader.GetString(1);
                var count = reader.GetInt32(2);
                var cur = byDay.GetValueOrDefault(day);
                byDay[day] = status switch
                {
                    nameof(SyncStatus.Success) => (cur.Success + count, cur.Failed),
                    nameof(SyncStatus.Failed) => (cur.Success, cur.Failed + count),
                    _ => cur,
                };
            }
        }

        var result = new List<DailyTrendPoint>();
        for (var day = fromLocal; day <= toLocalInclusive; day = day.AddDays(1))
        {
            var v = byDay.GetValueOrDefault(day.ToString("yyyy-MM-dd"));
            result.Add(new DailyTrendPoint(day, v.Success, v.Failed));
        }
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

    // BIR YEREL GUNUN TUM KAYITLARI -- ORTAMA KISITLI (2026-10-05, gun sonu raporu icin).
    //
    // NEDEN AYRI BIR METOT: QueryAsync, GetStatusCountsAsync ve GetDailyTrendAsync ortam
    // filtresi UYGULAMIYOR ve bu bilincli -- onlar GECMIS/EKRAN sorgulari, Aktivite
    // Akisi'nda her iki ortamin kaydini gormek dogru. Ama gun sonu raporu bir OLCUM:
    // "dun 412 kayit gonderildi" cumlesi canli ortamdayken sandbox denemelerini sayarsa
    // dogrudan yalan olur. Mevcut sorgulari degistirmek Aktivite ekranini da degistirirdi,
    // bu yuzden rapora kendi sorgusu yazildi.
    //
    // GUN SINIRI YEREL: Baki saatiyle 00:00-24:00. UTC'ye gore gruplamak gece nobetinde
    // (00:00-04:00) yapilan gonderimleri bir onceki gune dusururdu -- bu hata bu projede
    // gunluk trend grafiginde bir kez yasandi (bkz. AzTime).
    public async Task<List<SyncLogEntry>> GetGunKayitlariAsync(
        DateOnly gunYerel, CancellationToken ct = default)
    {
        var baslangicUtc = AzTime.ToUtc(gunYerel.ToDateTime(TimeOnly.MinValue));
        var bitisUtc = AzTime.ToUtc(gunYerel.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var ortam = await OrtamAsync(ct);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        // RequestJson/ResponseJson OKUNMUYOR: rapor yalnizca durum ve mesaja bakiyor,
        // govdeler bir gunde yuz binlerce satirda ciddi bellek demek (ayni gerekce
        // GetLatestByPusulaIdsAsync'teki govdeleriGetir:false icin de gecerli).
        cmd.CommandText = @"
            SELECT Id, ResourceType, PusulaId, Status, Operation, AzResourceId, Message,
                   NULL, NULL, PatientFullName, FathersName, BirthDate, Gender, Fin,
                   RecordOpenedAt, CreatedAtUtc, Ortam
            FROM SyncLog
            WHERE CreatedAtUtc >= $from AND CreatedAtUtc < $to
              AND Ortam = $ortam
            ORDER BY Id";
        cmd.Parameters.AddWithValue("$from", baslangicUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$to", bitisUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$ortam", ortam);

        var sonuc = new List<SyncLogEntry>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sonuc.Add(ReadEntry(reader));
        return sonuc;
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
        // TARIH AYRISTIRMA -- kultur ve Kind ACIKCA belirtiliyor (2026-09-28 inceleme).
        // Sunucu tr-TR kulturuyle calisiyor (kisa tarih kalibi "d.MM.yyyy"). Olculdu:
        // bu ISO bicimleri tr-TR altinda da dogru ayrisiyor, yani mevcut veride bir hata
        // YOK. Yine de kulture birakmak kirilgan.
        //
        // RoundtripKind asil korumayi sagliyor: sonunda "Z" olan deger Kind=Utc olarak
        // okunuyor. Bu olmadan .NET onu Kind=Local yapiyordu ve dogru sonuc yalnizca
        // ToUniversalTime'in geri cevirmesi sayesinde cikiyordu. Bir gun CreatedAtUtc
        // Kind=Unspecified bir degerle yazilirsa (yani "O" sonekSIZ uretirse) eski hal
        // sessizce 4 SAAT kaydirirdi -- GetDailyTrendAsync'te bir kez yasanan hatanin
        // aynisi.
        BirthDate = reader.IsDBNull(11) ? null : DateOnly.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
        Gender = reader.IsDBNull(12) ? null : reader.GetString(12),
        Fin = reader.IsDBNull(13) ? null : reader.GetString(13),
        // RecordOpenedAt Pusula'nin YEREL saati -- soneksiz saklanir, Unspecified kalmali.
        RecordOpenedAt = reader.IsDBNull(14) ? null : DateTime.Parse(
            reader.GetString(14), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CreatedAtUtc = DateTime.Parse(
            reader.GetString(15), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
        // Eski kayitlarda NULL olabilir (sutun 2026-10-05'te eklendi); o satirlarin tamami
        // sandbox'a gitmisti, bu yuzden varsayilan Test.
        Ortam = reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : "Test",
    };
}
