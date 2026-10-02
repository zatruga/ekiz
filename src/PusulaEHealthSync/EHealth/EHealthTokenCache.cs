namespace PusulaEHealthSync.EHealth;

// TRƏS oturum token'ini TUM EHealthClient ornekleri arasinda paylasir.
//
// NEDEN GEREKTI (2026-09-28 incelemesi): token, EHealthClient'in kendi alanindaydi
// (_sessionToken). EHealthClient ise AddHttpClient<EHealthClient>() ile kaydedildigi
// icin TRANSIENT -- yani her enjeksiyonda YENI bir ornek uretiliyor. Sonuclari:
//
//   1. Her senkron servisi (PatientSyncService, ProcedureSyncService, ... 13 adet)
//      kendi EHealthClient ornegini yakaladigi icin HER BIRI AYRI token aliyordu.
//   2. Razor sayfalari istek basina olusturuldugundan, EHealthClient kullanan HER
//      SAYFA ACILISI bakanlik sunucusuna yeni bir /auth/token POST'u atiyordu --
//      yani her sayfa yuklemesine fazladan bir ag gidis-donusu biniyordu.
//   3. Token alanlari kilitsizdi. Ayni anda iki istek 401 alirsa ikisi de token'i
//      null'layip ayni anda yeniden kimlik dogruluyordu; arada kalan ucuncu bir istek
//      ise null token ile gonderilip yine 401 alabiliyordu. Otomatik gonderim
//      donguse ile arayuz ayni anda calistiginda bu gercek bir yaris durumu.
//
// Bu sinif singleton olarak kaydedilir; token tek yerde durur ve yenileme tek
// seferde yapilir.
public class EHealthTokenCache
{
    private readonly SemaphoreSlim _kilit = new(1, 1);
    private string? _token;
    private EHealthEndpoint? _endpoint;

    // Gecerli token'i doner; yoksa (ya da ortam degistiyse) bir kez alir.
    public async Task<string> GetAsync(
        EHealthEndpoint endpoint,
        Func<EHealthEndpoint, CancellationToken, Task<string>> tokenAl,
        CancellationToken ct)
    {
        // EHealthEndpoint bir record -- deger esitligi sayesinde "ayni ortam mi"
        // karsilastirmasi adres/kullanici/sifre/provider dortlusu uzerinden yapilir.
        if (_token is { } mevcut && _endpoint == endpoint) return mevcut;

        await _kilit.WaitAsync(ct);
        try
        {
            if (_token is { } yine && _endpoint == endpoint) return yine;
            var yeni = await tokenAl(endpoint, ct);
            _token = yeni;
            _endpoint = endpoint;
            return yeni;
        }
        finally { _kilit.Release(); }
    }

    // 401 sonrasi yenileme. bayatToken: istegin kullandigi (artik gecersiz) token.
    //
    // Kilit icinde saklanan token'in HALA o bayat deger olup olmadigina bakiyoruz:
    // degilse baska bir istek bizden once yenilemis demektir, onun aldigi token
    // kullanilir. Boylece es zamanli 401'ler tek bir /auth/token cagrisina dusuyor.
    public async Task<string> RefreshAsync(
        EHealthEndpoint endpoint,
        Func<EHealthEndpoint, CancellationToken, Task<string>> tokenAl,
        string? bayatToken,
        CancellationToken ct)
    {
        await _kilit.WaitAsync(ct);
        try
        {
            if (_token is { } guncel && _endpoint == endpoint && guncel != bayatToken)
                return guncel;

            var yeni = await tokenAl(endpoint, ct);
            _token = yeni;
            _endpoint = endpoint;
            return yeni;
        }
        finally { _kilit.Release(); }
    }
}
