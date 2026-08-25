using Microsoft.Data.Sqlite;

namespace PusulaEHealthSync.Persistence;

// Basit key-value ayar deposu -- SyncLog ile ayni SQLite dosyasinda, ayri bir tablo.
// Ilk kullanim alani: acik protokollerin kac gun sonra (kapanmasa bile) Encounter
// gonderimine "uygun" sayilacagi (bkz. Ayarlar sayfasi + EncounterMapper.IsEligible).
// Ileride baska parametreler eklenirse ayni tabloya yeni Key'ler olarak eklenebilir --
// sema degisikligi gerekmez.
public class SettingsStore
{
    public const string OpenProtokolSendAfterDaysKey = "OpenProtokolSendAfterDays";
    public const int OpenProtokolSendAfterDaysDefault = 7;

    // -- Ortam / Endpoint (Test - Canlı) ------------------------------------------------
    // Bos birakilirsa EHealthClient appsettings/user-secrets'taki EHealthOptions'a (mevcut
    // sabit Test/sandbox degerleri) duser -- bu yuzden mevcut davranis hicbir sey
    // girilmeden de calismaya devam eder.
    public const string EHealthEnvironmentKey = "EHealth.Environment"; // "Test" | "Live"
    public const string EHealthEnvironmentDefault = "Test";
    public const string EHealthTestBaseUrlKey = "EHealth.Test.BaseUrl";
    public const string EHealthTestUserNameKey = "EHealth.Test.UserName";
    public const string EHealthTestPasswordKey = "EHealth.Test.Password";
    public const string EHealthTestProviderIdKey = "EHealth.Test.ProviderId";
    public const string EHealthLiveBaseUrlKey = "EHealth.Live.BaseUrl";
    public const string EHealthLiveUserNameKey = "EHealth.Live.UserName";
    public const string EHealthLivePasswordKey = "EHealth.Live.Password";
    public const string EHealthLiveProviderIdKey = "EHealth.Live.ProviderId";

    // -- Otomatik gonderim (genel) -------------------------------------------------------
    public const string AutoSendPatientEnabledKey = "AutoSend.Patient.Enabled";
    public const string AutoSendEncounterEnabledKey = "AutoSend.Encounter.Enabled";
    public const string AutoSendIntervalMinutesKey = "AutoSend.IntervalMinutes";
    public const int AutoSendIntervalMinutesDefault = 60;
    public const string AutoSendBatchSizeKey = "AutoSend.BatchSize";
    public const int AutoSendBatchSizeDefault = 50;

    // -- Hata sonrasi tekrar deneme -------------------------------------------------------
    public const string RetryIntervalMinutesKey = "Retry.IntervalMinutes";
    public const int RetryIntervalMinutesDefault = 30;
    public const string RetryMaxAttemptsKey = "Retry.MaxAttempts";
    public const int RetryMaxAttemptsDefault = 5;

    // -- Kaynak bazli kurallar --------------------------------------------------------------
    // Epikriz (Composition) YAZILDI (2026-08-20) -- kullanici istegi: (1) gonderim ayri
    // acilip kapatilabilsin (ileride "epikrizi gonderme" diyebilmek icin), (2) sadece
    // Pusula'da TAMAMLANMIS epikrizler gonderilsin. "Tamamlanmis" icin EpikrizTamamlanmaTarihi
    // KULLANILMADI -- canli veride (son 30 gun) 0 kayitta doluydu, yani pratikte hic
    // kullanilmiyor. Bunun yerine GenelMuayene.KilitDurumuId=1 ("kilitli" -- hekim notu
    // tamamlayip kilitledi) gercek sinyal olarak kullanildi, bkz. CompositionMapper/
    // GenelMuayeneRecord.IsLocked. Varsayilan: gonderim ACIK, sadece kilitli olanlar
    // (ikisi de "guvenli/beklenen" varsayilan -- kullanici aksini secene kadar).
    public const string EpikrizSendEnabledKey = "Epikriz.SendEnabled";
    public const string EpikrizOnlySignedKey = "Epikriz.OnlySigned";

    // Tanı (Condition) / İşlem (Procedure) -- KULLANICI ISTEGI (2026-08-25): "ama bence
    // göndermeyi otomatik yapabiliriz ... yada bunu parametrik yapıp isteğe göre
    // değiştirilebilir olmalı" -- otomatik cascade (Müayinə ile birlikte) davranışı
    // korunuyor ama Ayarlar'dan kapatılabilir hale getirildi. Varsayılan: ikisi de ACIK
    // (mevcut davranış hiçbir şey değiştirilmeden aynen devam eder).
    public const string ConditionSendEnabledKey = "Condition.SendEnabled";
    public const string ProcedureSendEnabledKey = "Procedure.SendEnabled";

    // Lab (DiagnosticReport) HENUZ YAZILMADI -- veri kaynagi netlesmedi (bkz. konusma
    // 2026-08-20): legacy LIS.TestIslem/NumuneIslem tablolari bos (0 satir, "-old" suffix'li
    // arsiv), yeni [EMR.Laboratory].[Order] tablosu ise sadece siparis metadata'si tutuyor
    // (numune bolgesi, endikasyon), sonuc DEGERI/onay durumu icin ayri bir tablo/kaynak
    // henuz bulunamadi. Anahtar burada dursun (Ayarlar sayfasinda kullanilmiyor) -- mapper
    // yazilinca ayni kalip (SendEnabled + OnlyVerified) uygulanacak.
    public const string LabOnlyVerifiedKey = "Lab.OnlyVerified";

    // -- Gunluk e-posta raporu ------------------------------------------------------------
    public const string MailEnabledKey = "Mail.Enabled";
    public const string MailSmtpHostKey = "Mail.SmtpHost";
    public const string MailSmtpHostDefault = "mail.mlpcare.com";
    public const string MailSmtpPortKey = "Mail.SmtpPort";
    public const int MailSmtpPortDefault = 25;
    public const string MailUseTlsKey = "Mail.UseTls";
    public const string MailUsernameKey = "Mail.Username";
    public const string MailPasswordKey = "Mail.Password";
    public const string MailFromAddressKey = "Mail.FromAddress";
    public const string MailFromAddressDefault = "pusula-ehealth@mlpcare.com";
    public const string MailSendHourKey = "Mail.SendHour";
    public const int MailSendHourDefault = 7;
    public const string MailRecipientsKey = "Mail.Recipients";

    private readonly string _connectionString;

    public SettingsStore(string dbPath)
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
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is string s && int.TryParse(s, out var i) ? i : defaultValue;
    }

    public async Task SetIntAsync(string key, int value, CancellationToken ct = default)
        => await SetStringAsync(key, value.ToString(), ct);

    public async Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken ct = default)
    {
        var value = await GetStringOrNullAsync(key, ct);
        return value is null ? defaultValue : value == "1";
    }

    public async Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
        => await SetStringAsync(key, value ? "1" : "0", ct);

    public async Task<string> GetStringAsync(string key, string defaultValue, CancellationToken ct = default)
        => await GetStringOrNullAsync(key, ct) ?? defaultValue;

    private async Task<string?> GetStringOrNullAsync(string key, CancellationToken ct)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value as string;
    }

    public async Task SetStringAsync(string key, string value, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Settings (Key, Value) VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
