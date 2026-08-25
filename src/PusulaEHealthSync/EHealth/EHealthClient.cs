using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Config;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.EHealth;

// Test/Canli ortam adresi ve kimlik bilgileri artik sabit degil -- Ayarlar sayfasindan
// SettingsStore'a yazilan degerler her istekte yeniden okunur (bkz. ResolveEndpointAsync).
// Hicbir sey ayarlanmamissa appsettings/user-secrets'taki EHealthOptions (mevcut Test/
// sandbox degerleri) fallback olarak kullanilir -- bu yuzden mevcut davranis degismez.
public class EHealthClient
{
    private readonly HttpClient _http;
    private readonly EHealthOptions _options;
    private readonly SettingsStore _settings;
    private readonly ILogger<EHealthClient> _logger;
    private string? _sessionToken;
    private EHealthEndpoint? _tokenEndpoint;

    public EHealthClient(HttpClient http, IOptions<EHealthOptions> options, SettingsStore settings, ILogger<EHealthClient> logger)
    {
        _http = http;
        _options = options.Value;
        _settings = settings;
        _logger = logger;
    }

    private async Task<EHealthEndpoint> ResolveEndpointAsync(CancellationToken ct)
    {
        var environment = await _settings.GetStringAsync(SettingsStore.EHealthEnvironmentKey, SettingsStore.EHealthEnvironmentDefault, ct);
        if (environment == "Live")
        {
            return new EHealthEndpoint(
                await OverrideOrAsync(SettingsStore.EHealthLiveBaseUrlKey, "", ct),
                await OverrideOrAsync(SettingsStore.EHealthLiveUserNameKey, "", ct),
                await OverrideOrAsync(SettingsStore.EHealthLivePasswordKey, "", ct),
                await OverrideOrAsync(SettingsStore.EHealthLiveProviderIdKey, "", ct));
        }

        return new EHealthEndpoint(
            await OverrideOrAsync(SettingsStore.EHealthTestBaseUrlKey, _options.BaseUrl, ct),
            await OverrideOrAsync(SettingsStore.EHealthTestUserNameKey, _options.UserName, ct),
            await OverrideOrAsync(SettingsStore.EHealthTestPasswordKey, _options.Password, ct),
            await OverrideOrAsync(SettingsStore.EHealthTestProviderIdKey, _options.ProviderId, ct));
    }

    // Ayarlar sayfasindaki bir alan BOS birakilip kaydedilirse SettingsStore'a "" olarak
    // yazilir (satir var ama degeri bos) -- SettingsStore.GetStringAsync'in normal null-
    // coalescing fallback'i bu durumu YAKALAMAZ (bos string null degildir). Bu yuzden
    // burada ayrica whitespace kontrolu yapiyoruz; boylece bir alani bilerek bos birakmak,
    // appsettings/user-secrets'taki (Test icin) ya da bos (Live icin) degere donmeyi
    // BOZMAZ.
    private async Task<string> OverrideOrAsync(string key, string fallback, CancellationToken ct)
    {
        var stored = await _settings.GetStringAsync(key, "", ct);
        return string.IsNullOrWhiteSpace(stored) ? fallback : stored;
    }

    // $validate: kaynagi kalici olarak KAYDETMEZ, sadece dogrular. Gercek veriyle
    // denerken bile bu yuzden POST/PUT'a gore daha guvenli bir ilk adimdir.
    public Task<EHealthResult> ValidateAsync(string resourceType, JsonObject resource, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"/fhir/{resourceType}/$validate", resource, ct);

    public Task<EHealthResult> CreateAsync(string resourceType, JsonObject resource, CancellationToken ct = default)
        => SendAsync(HttpMethod.Post, $"/fhir/{resourceType}", resource, ct);

    // FHIR PUT kurali: govdedeki id, URL'deki id ile AYNI olmali -- "Resource ID mismatch"
    // hatasi (2026-08-20, canli test) bunu kanitladi. Bizim mapper'larimiz her zaman kendi
    // sema id'sini (orn. patient-{PusulaId}) yazar; burada sunucunun GERCEK (server-assigned)
    // id'siyle degistiriyoruz, cagiran taraf bunu ayrica dusunmek zorunda kalmasin.
    public Task<EHealthResult> UpdateAsync(string resourceType, string id, JsonObject resource, CancellationToken ct = default)
    {
        resource["id"] = id;
        return SendAsync(HttpMethod.Put, $"/fhir/{resourceType}/{id}", resource, ct);
    }

    // CapabilityStatement'ta Patient/Encounter icin "delete" interaction'i destekleniyor
    // (docs/sql-exports/capability-statement.json) -- yanlislikla gonderilen bir kaydi
    // geri almak icin kullanilir (bkz. DeleteService).
    public async Task<EHealthResult> DeleteAsync(string resourceType, string id, CancellationToken ct = default)
    {
        var raw = await SendRawAsync(HttpMethod.Delete, $"/fhir/{resourceType}/{id}", null, ct);
        return raw.Success ? EHealthResult.Ok(raw.Body) : EHealthResult.Fail(raw.StatusCode, raw.Body);
    }

    // Bilinen bir AZ kaynak id'si icin e-Health'te SU AN ne var, dogrudan okur. "Gonderdigim
    // veri gercekten dogru mu/dogru yere mi gitti" kontrolu icin -- SendOnceAsync'teki
    // RequestJson/ResponseJson sadece o anki denemenin GONDERILEN/DONEN gövdesini tutar,
    // bu ise SORGU aninda bakanlikta GERCEKTE ne oldugunu gosterir (aradan baska bir
    // Update/Delete gecmis olabilir).
    public async Task<EHealthResult> GetAsync(string resourceType, string id, CancellationToken ct = default)
    {
        var raw = await SendRawAsync(HttpMethod.Get, $"/fhir/{resourceType}/{id}", null, ct);
        return raw.Success ? EHealthResult.Ok(raw.Body) : EHealthResult.Fail(raw.StatusCode, raw.Body);
    }

    // local-system-unique-id ile arar; kayit varsa FHIR id'sini, yoksa null doner.
    // updateCreate=false oldugu icin (CapabilityStatement'ta dogrulandi) PUT'tan once
    // her zaman bu arama yapilmali.
    public async Task<string?> FindExistingIdAsync(string resourceType, string localSystemUniqueId, CancellationToken ct = default)
    {
        var path = $"/fhir/{resourceType}?local-system-unique-id={Uri.EscapeDataString(localSystemUniqueId)}";
        var result = await SendRawAsync(HttpMethod.Get, path, null, ct);
        if (!result.Success || result.Body is null) return null;

        var bundle = JsonNode.Parse(result.Body)?.AsObject();
        var entries = bundle?["entry"]?.AsArray();
        if (entries is null || entries.Count == 0) return null;

        return entries[0]?["resource"]?["id"]?.GetValue<string>();
    }

    private async Task<EHealthResult> SendAsync(HttpMethod method, string path, JsonObject body, CancellationToken ct)
    {
        var raw = await SendRawAsync(method, path, body.ToJsonString(JsonDefaults.Options), ct);
        if (raw.Success)
            return EHealthResult.Ok(raw.Body);
        return EHealthResult.Fail(raw.StatusCode, raw.Body);
    }

    private async Task<RawResult> SendRawAsync(HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        var endpoint = await ResolveEndpointAsync(ct);
        await EnsureTokenAsync(endpoint, ct);
        var response = await SendOnceAsync(endpoint, method, path, jsonBody, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Token gecersiz/suresi dolmus, yenileniyor ve istek tekrarlaniyor.");
            _sessionToken = null;
            await EnsureTokenAsync(endpoint, ct);
            response = await SendOnceAsync(endpoint, method, path, jsonBody, ct);
        }

        var content = await response.Content.ReadAsStringAsync(ct);
        return new RawResult(response.IsSuccessStatusCode, (int)response.StatusCode, content);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(EHealthEndpoint endpoint, HttpMethod method, string path, string? jsonBody, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, BuildUri(endpoint.BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _sessionToken);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, ct);
    }

    private async Task EnsureTokenAsync(EHealthEndpoint endpoint, CancellationToken ct)
    {
        if (_sessionToken is not null && _tokenEndpoint == endpoint) return;

        if (string.IsNullOrWhiteSpace(endpoint.BaseUrl))
            throw new InvalidOperationException("Aktif ortam (Test/Canlı) için e-Health adresi tanımlı değil -- Ayarlar sayfasından girin.");

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(endpoint.BaseUrl, "/auth/token"));
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{endpoint.UserName}:{endpoint.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        var body = new JsonObject { ["healthcareProviderId"] = endpoint.ProviderId };
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json-patch+json");

        var response = await _http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"e-Health kimlik doğrulama başarısız (HTTP {(int)response.StatusCode} {endpoint.BaseUrl}): {content}");

        var json = JsonNode.Parse(content)?.AsObject();
        _sessionToken = json?["payload"]?["sessionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Token yaniti sessionId icermiyor: " + content);
        _tokenEndpoint = endpoint;
    }

    private static Uri BuildUri(string baseUrl, string path)
        => new(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));

    private record RawResult(bool Success, int StatusCode, string Body);
}

// Value-equality (record) sayesinde EnsureTokenAsync, ortam degismedigi surece token'i
// yeniden kullanabiliyor -- her istekte SettingsStore'dan okunsa bile gereksiz yeniden
// kimlik dogrulama yapilmiyor.
public record EHealthEndpoint(string BaseUrl, string UserName, string Password, string ProviderId);

public record EHealthResult(bool Success, int? StatusCode, string? Body)
{
    public static EHealthResult Ok(string? body) => new(true, 200, body);
    public static EHealthResult Fail(int statusCode, string? body) => new(false, statusCode, body);
}
