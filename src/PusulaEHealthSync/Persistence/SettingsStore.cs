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

    // AYNI GUN GONDERILMEZ (KULLANICI KARARI 2026-10-03: "gönderim yaparken 1 gün öncesini
    // göndersin, hiç aynı gün göndermeyelim"). Protokol kapandigi gun kayit hala hareketli:
    // geciken laboratuvar sonucu dusebilir, hekim epikrizi duzeltebilir. Bir gun beklemek
    // kaydi oturtuyor ve sonradan guncelleme/iptal gonderme ihtiyacini azaltiyor.
    // Olcut takvim gunu farki -- 24 saat degil (bkz. PendingWorkService.IsEligible).
    public const string MinProtokolYasiGunKey = "Send.MinProtokolYasiGun";
    public const int MinProtokolYasiGunDefault = 1;

    // -- Kaynak veritabani baglantisi -----------------------------------------------------
    // KULLANICI ISTEGI (2026-08-28): tek bir "connection string" alani yerine ayri Sunucu/
    // Veritabani/Kullanici/Sifre alanlari -- kullanicinin ADO.NET sozdizimi bilmesine gerek
    // kalmasin diye. Ucu bos birakilirsa (Server/Name/User'dan biri eksikse) PusulaRepository
    // appsettings/user-secrets'taki PusulaOptions.ConnectionString'e duser -- EHealth ortam
    // ayarlariyla ayni kalip (bkz. PusulaRepository.ConnectionStringAsync), mevcut davranis
    // hicbir sey girilmeden de calisir.
    public const string PusulaDbServerKey = "Pusula.Db.Server";
    public const string PusulaDbNameKey = "Pusula.Db.Name";
    public const string PusulaDbUserKey = "Pusula.Db.User";
    public const string PusulaDbPasswordKey = "Pusula.Db.Password";

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

    // -- Bekleyen is taramasi araligi -----------------------------------------------------
    // KULLANICI ISTEGI (2026-09-28): "bu tarama tum data degil, Ayarlar kismina alan
    // ekleyelim; orada girilen tarihten itibaren datayi al; bir de son 1 ay / son 3 ay
    // gibi bir alan olsun -- istenirse tarih, istenirse secilen tanima gore filtre."
    //
    // NEDEN GEREKTI: tarama penceresi kodda sabit 60 gundu ve maliyeti olculmemisti.
    // Canli olcum (2026-09-28): 60 gunluk pencere 850.327 aday satir cekiyor (islem
    // 426.539 + laboratuvar 405.396 + digerleri). Otomatik gonderim acildiginda bu HER
    // SAAT tekrarlanacakti. Artik pencere ekrandan ayarlanabiliyor.
    //
    // Mod "Preset" ise PendingScan.PresetDays gun geriye bakilir; "Date" ise
    // PendingScan.FromDate (yyyy-MM-dd) tarihinden ITIBAREN taranir.
    public const string PendingScanModeKey = "PendingScan.Mode";      // "Preset" | "Date"
    public const string PendingScanModeDefault = "Preset";
    public const string PendingScanPresetDaysKey = "PendingScan.PresetDays";
    public const int PendingScanPresetDaysDefault = 60;
    public const string PendingScanFromDateKey = "PendingScan.FromDate";

    // -- Artimli tarama (otomatik gonderim dongusu) ----------------------------------------
    // KULLANICI SORUSU (2026-09-29): "neden 885 bin satirlik veri cekiyoruz, amacimiz hep
    // bugun gonderileceklerin listesi degil mi?" -- hakliydi. Eski yontem her turda
    // "yapilandirilmis pencerede TAMAMLANAN her seyi" cekip bellekte "gonderdiklerimi"
    // cikariyordu. Artimli tarama ayni sonucu cok daha kucuk bir sorguyla buluyor.
    //
    // ISARET (Watermark): son BASARILI otomatik taramanin BASLADIGI an (UTC). Bir sonraki
    // tur buradan itibaren bakar. Bitis degil baslangic kaydediliyor -- tarama surerken
    // tamamlanan kayitlar atlanmasin diye.
    //
    // EN ESKI BEKLEYEN: hala gonderilememis kalemlerin en eskisinin tarihi. Pencere bunun
    // gerisine ASLA cekilmez, yoksa takilan kayit listeden duser ve sistemin kendini
    // onarma ozelligi kaybolurdu.
    //
    // TAM SUPURME: gunde bir kez tam pencere taranir. Geriye donuk duzeltmeleri (biri
    // dunun onay tarihini elle degistirirse) artimli tarama kaciririr; bu onun guvenlik agi.
    public const string PendingScanIncrementalEnabledKey = "PendingScan.Incremental.Enabled";
    public const string PendingScanWatermarkKey = "PendingScan.Watermark";
    public const string PendingScanOldestPendingKey = "PendingScan.OldestPending";
    public const string PendingScanFullSweepHourKey = "PendingScan.FullSweepHour";
    public const int PendingScanFullSweepHourDefault = 3;               // gece 03:00
    public const string PendingScanLastFullSweepKey = "PendingScan.LastFullSweep";

    // TAKILANLARIN AYRI DENEMESI (KULLANICI KARARI 2026-10-03: "onlar için ayrıca deneme
    // yapsın"). Mevcut veriyle gonderilemeyen kalemler ana turda hic denenmiyor; gunde bir
    // kez, ayri bir turda deneniyor. Boylece eksik giderilince (LOINC kodu verilince, sonuc
    // girilince) kayit kendiliginden gidiyor, ama saatlik dongu onlarla ugrasmiyor.
    public const string StuckRetryHourKey = "AutoSend.StuckRetryHour";
    public const int StuckRetryHourDefault = 4;                          // gece 04:00
    public const string StuckRetryLastRunKey = "AutoSend.StuckRetryLastRun";
    public const string StuckRetryBatchSizeKey = "AutoSend.StuckRetryBatchSize";
    public const int StuckRetryBatchSizeDefault = 100;

    // Saat kaymasi ve gec commit olan islemler icin guvenlik payi: isaretin biraz
    // GERISINDEN baslanir. Fazladan birkac yuz satir okumak, bir kaydi kacirmaktan iyidir.
    public const int PendingScanOverlapMinutes = 120;

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

    // Radyoloji (DiagnosticReport) -- Procedure ile AYNI cascade noktasindan (Encounter
    // basariyla yazildiktan sonra) tetiklenir ama KENDI ayri anahtariyla acilip kapatilabilir
    // (Procedure gonderimini kapatmak radyolojiyi de kapatmamali, ikisi ayri karar). Varsayilan:
    // ACIK. Bkz. EncounterSyncService.SyncRadiologyReportsAsync.
    public const string RadiologyReportSendEnabledKey = "RadiologyReport.SendEnabled";

    // Patoloji (DiagnosticReport) -- Radyoloji ile AYNI cascade noktasi ve AYNI acik/kapali
    // mantigi, kendi ayri anahtariyla. Bkz. EncounterSyncService.SyncPathologyReportsAsync.
    public const string PathologyReportSendEnabledKey = "PathologyReport.SendEnabled";

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
        _connectionString = SqliteDb.ConnectionString(dbPath);
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

    // KISA OMURLU ANLIK GORUNTU ONBELLEGI (2026-09-28 inceleme bulgusu)
    //
    // Bu tablo cok kucuk (birkac dusuna satir) ama INANILMAZ sik okunuyordu: her ayar
    // okumasi kendi SQLite baglantisini aciyordu ve cagiranlar tek seferde birden cok
    // anahtar istiyor:
    //   - EHealthClient.ResolveEndpointAsync -> HER TRƏS isteginde 5 okuma
    //   - PusulaRepository.ConnectionStringAsync -> HER Pusula sorgusunda 3-4 okuma
    // Yani tek bir protokolun gonderimi yuzlerce gereksiz baglanti acip kapatiyordu.
    //
    // Cozum: tablonun TAMAMI tek sorguyla okunup kisa sure bellekte tutuluyor.
    //
    // NEDEN SURESIZ DEGIL: bu dosyayi IKI SURECI birden kullaniyor (Web + Worker
    // servisi, bkz. SqliteDb). Web'de yapilan bir ayar degisikligini Worker'in gormesi
    // gerekiyor. Sonsuz onbellek bunu kalici olarak bozardi. 3 saniyelik omur, tek bir
    // gonderim turundaki yuzlerce okumayi tek okumaya indirirken sureclerarasi
    // gecikmeyi insanin fark edemeyecegi bir seviyede tutuyor.
    //
    // Ayni surecteki yazma ise BEKLEMEZ: SetStringAsync onbellegi aninda dusurur, yani
    // Ayarlar sayfasindan kaydedilen deger o istekte hemen gecerli olur.
    private static readonly TimeSpan OnbellekOmru = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _yenilemeKilidi = new(1, 1);
    private volatile Dictionary<string, string>? _onbellek;
    private DateTime _onbellekSonKullanmaUtc = DateTime.MinValue;

    private async Task<Dictionary<string, string>> AnlikGoruntuAsync(CancellationToken ct)
    {
        var mevcut = _onbellek;
        if (mevcut is not null && DateTime.UtcNow < _onbellekSonKullanmaUtc) return mevcut;

        await _yenilemeKilidi.WaitAsync(ct);
        try
        {
            // Kilidi beklerken baska biri tazelemis olabilir -- tekrar bak.
            mevcut = _onbellek;
            if (mevcut is not null && DateTime.UtcNow < _onbellekSonKullanmaUtc) return mevcut;

            var taze = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var conn = new SqliteConnection(_connectionString))
            {
                await conn.OpenAsync(ct);
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Key, Value FROM Settings";
                using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    taze[reader.GetString(0)] = reader.GetString(1);
            }

            _onbellek = taze;
            _onbellekSonKullanmaUtc = DateTime.UtcNow.Add(OnbellekOmru);
            return taze;
        }
        finally { _yenilemeKilidi.Release(); }
    }

    private void OnbellegiDusur() => _onbellekSonKullanmaUtc = DateTime.MinValue;

    public async Task<int> GetIntAsync(string key, int defaultValue, CancellationToken ct = default)
    {
        var value = await GetStringOrNullAsync(key, ct);
        return value is not null && int.TryParse(value, out var i) ? i : defaultValue;
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
        => (await AnlikGoruntuAsync(ct)).GetValueOrDefault(key);

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
        OnbellegiDusur();
    }
}
