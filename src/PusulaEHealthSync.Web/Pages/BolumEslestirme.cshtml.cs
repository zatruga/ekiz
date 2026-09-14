using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Web.Pages;

// Bolum Eslestirme -- Pusula Ortak.Bolum'u AZ hospital-departments koduna BIREBIR,
// elle eslestirme ekrani. KARAR (2026-08-20, kullanici istegi): otomatik isim-bazli
// eslestirme + "Digər"(999) fallback yaklasimi terk edildi ("Dermatologiya" gibi
// belirsiz/yanlis eslesme riski) -- artik SADECE burada acikca eslestirilmis bolumler
// Encounter'a serviceType olarak yazilabiliyor (bkz. EncounterMapper.Map).
public class BolumEslestirmeModel(PusulaRepository pusulaRepository, BolumMappingStore bolumMappingStore) : PageModel
{
    private const int UsageWindowDays = 365;

    // KULLANICI ISTEGI (2026-08-25): ust bilgi barindaki Eslestirildi/Eslestirilmedi
    // sayilari tiklanabilir olsun, tiklaninca listeyi filtrelesin.
    [BindProperty(SupportsGet = true)]
    public string Durum { get; set; } = "Tumu";

    private List<Row> _allRows = [];
    public List<Row> Rows { get; set; } = [];
    public int TotalCount { get; set; }
    public int MappedCount { get; set; }
    public int UnmappedCount { get; set; }
    public bool Saved { get; set; }

    // Ice aktarim sonucu -- null ise bu istekte ice aktarim yapilmadi.
    public string? ImportMessage { get; set; }
    public bool ImportFailed { get; set; }

    [BindProperty]
    public Dictionary<int, string?> Mappings { get; set; } = new();

    public record Row(int BolumId, string? Adi, int Adet, string? AzKod);

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Bolum adini (Adi) DB'ye yazmadan once _allRows'un dolu olmasi lazim -- eskiden bu
        // satirdan once hic yuklenmedigi icin Adi her zaman null kaydediliyordu (Sil/gonderim
        // mantigini etkilemiyordu, sadece BolumMapping tablosundaki isim kolonunu bozuyordu).
        await LoadAsync(ct);

        foreach (var (bolumId, azKod) in Mappings)
        {
            var adi = _allRows.FirstOrDefault(r => r.BolumId == bolumId)?.Adi;
            await bolumMappingStore.SetAsync(bolumId, adi, azKod, ct);
        }

        Saved = true;
        await LoadAsync(ct);
        return Page();
    }

    // ---- Disa / ice aktarim -------------------------------------------------
    //
    // NEDEN VAR (2026-09-14): eslestirmeler synclog.db'de duruyor ve her kurulumda
    // bos basliyor -- lokalde yapilan 65 eslestirme sunucuda yoktu. synclog.db'yi
    // butunuyle kopyalamak SyncLog/Settings/Users tablolarini da ezecegi icin
    // (gonderim gecmisi kaybolur) aktarim TABLO BAZLI yapiliyor.
    //
    // Eslestirme anahtari PusulaBolumId'dir; iki taraf da ayni Pusula veritabanini
    // okudugu icin Id'ler birebir ayni. Bolum adi dosyada sadece dogrulama icin.

    private const string ExportSchema = "pusula-ehealth/bolum-eslestirme";

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public record ExportRow(int PusulaBolumId, string? PusulaBolumAdi, string AzKod);
    public record ExportFile(string Sema, int Surum, string OlusturulmaUtc, List<ExportRow> Eslestirmeler);

    public async Task<IActionResult> OnGetDisaAktarAsync(CancellationToken ct)
    {
        var rows = await bolumMappingStore.GetAllRowsAsync(ct);
        var payload = new ExportFile(
            ExportSchema,
            1,
            DateTime.UtcNow.ToString("O"),
            rows.Where(r => !string.IsNullOrWhiteSpace(r.AzKod))
                .Select(r => new ExportRow(r.Id, r.Adi, r.AzKod!))
                .ToList());

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, ExportJson));
        return File(bytes, "application/json", "bolum-eslestirme.json");
    }

    // ICE AKTARIM SADECE EKLER/GUNCELLER, HIC SILMEZ: dosyada bulunmayan bir bolumun
    // sunucudaki eslestirmesine dokunulmaz ve AzKod'u bos olan satir atlanir. Boylece
    // yanlis/eksik bir dosya yuklemek mevcut eslestirmeleri yok edemez.
    public async Task<IActionResult> OnPostIceAktarAsync(IFormFile? dosya, CancellationToken ct)
    {
        await LoadAsync(ct);

        if (dosya is null || dosya.Length == 0)
        {
            ImportFailed = true;
            ImportMessage = "Dosya seçilmedi.";
            return Page();
        }

        ExportFile? payload;
        try
        {
            await using var stream = dosya.OpenReadStream();
            payload = await JsonSerializer.DeserializeAsync<ExportFile>(
                stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
        }
        catch (JsonException ex)
        {
            ImportFailed = true;
            ImportMessage = $"Dosya okunamadı (geçerli JSON değil): {ex.Message}";
            return Page();
        }

        if (payload is null || !string.Equals(payload.Sema, ExportSchema, StringComparison.Ordinal))
        {
            ImportFailed = true;
            ImportMessage = "Bu dosya bir bölüm eşleştirme dışa aktarımı değil (şema alanı uyuşmuyor).";
            return Page();
        }

        var gecerliKodlar = AzDepartments.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        var mevcut = await bolumMappingStore.GetAllRowsAsync(ct);
        var mevcutKod = mevcut.ToDictionary(r => r.Id, r => r.AzKod);

        int yeni = 0, degisen = 0, ayni = 0, atlanan = 0;
        var uyarilar = new List<string>();

        foreach (var row in payload.Eslestirmeler ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.AzKod)) { atlanan++; continue; }
            if (!gecerliKodlar.Contains(row.AzKod))
            {
                atlanan++;
                uyarilar.Add($"{row.PusulaBolumId} ({row.PusulaBolumAdi}): \"{row.AzKod}\" geçerli bir AZ kodu değil");
                continue;
            }

            if (mevcutKod.TryGetValue(row.PusulaBolumId, out var eski) && !string.IsNullOrWhiteSpace(eski))
            {
                if (string.Equals(eski, row.AzKod, StringComparison.Ordinal)) { ayni++; continue; }
                degisen++;
            }
            else
            {
                yeni++;
            }

            // Bolum adini bu sunucunun kendi Pusula okumasindan aliyoruz; dosyadaki ad
            // yalnizca dosya bos gelirse yedek. Boylece isim her zaman yerel gercege uyar.
            var adi = _allRows.FirstOrDefault(r => r.BolumId == row.PusulaBolumId)?.Adi ?? row.PusulaBolumAdi;
            await bolumMappingStore.SetAsync(row.PusulaBolumId, adi, row.AzKod, ct);
        }

        var ozet = new StringBuilder($"İçe aktarıldı -- {yeni} yeni, {degisen} güncellendi, {ayni} zaten aynıydı");
        if (atlanan > 0) ozet.Append($", {atlanan} atlandı");
        ozet.Append('.');
        if (uyarilar.Count > 0) ozet.Append(" Atlananlar: ").Append(string.Join("; ", uyarilar.Take(10)));
        ImportMessage = ozet.ToString();

        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var usage = await pusulaRepository.GetUsedDepartmentsAsync(UsageWindowDays, ct);
        var mapping = await bolumMappingStore.GetAllAsync(ct);

        _allRows = usage
            .Select(u => new Row(u.BolumId, u.Adi, u.Adet, mapping.GetValueOrDefault(u.BolumId)))
            .OrderByDescending(r => r.Adet)
            .ToList();
        TotalCount = _allRows.Count;
        MappedCount = _allRows.Count(r => !string.IsNullOrWhiteSpace(r.AzKod));
        UnmappedCount = TotalCount - MappedCount;

        Rows = Durum switch
        {
            "Eslestirildi" => _allRows.Where(r => !string.IsNullOrWhiteSpace(r.AzKod)).ToList(),
            "Eslestirilmedi" => _allRows.Where(r => string.IsNullOrWhiteSpace(r.AzKod)).ToList(),
            _ => _allRows,
        };
    }

    public static IReadOnlyList<KeyValuePair<string, string>> AzDepartments { get; } =
        EncounterMapper.HospitalDepartments
            .OrderBy(kv => int.Parse(kv.Key))
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .Append(new KeyValuePair<string, string>("999", "Digər"))
            .ToList();
}
