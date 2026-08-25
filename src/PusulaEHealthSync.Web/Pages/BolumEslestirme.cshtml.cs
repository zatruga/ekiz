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
