using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// Doktorlar -- sistem-genelinde doktor (Practitioner) yonetim ekrani. KULLANICI ISTEGI
// (2026-08-24): "doktor takibini Protokol Detay'dan kaldiralim, sistem tarafinda doktorlar
// diye ayri bir yer olsun". EncounterSyncService artik bir doktoru HER protokolde otomatik
// tekrar denemiyor (bkz. o dosyadaki KARAR notu) -- basarisiz olan bir doktoru elle tekrar
// gondermek/kontrol etmek icin tek yer burasi.
public class DoktorlarModel(PusulaRepository pusulaRepository, SyncLogStore syncLog, PractitionerSyncService practitionerSyncService, DeleteService deleteService) : PageModel
{
    private const int UsageWindowDays = 180;

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    // KULLANICI ISTEGI (2026-08-25): ust bilgi barindaki Gonderildi/Hatali/Gonderilmedi
    // sayilari tiklanabilir olsun, tiklaninca listeyi filtrelesin -- Index.cshtml'deki
    // HastaDurumu/ProtokolDurumu ile ayni GET-tabanli filtre deseni.
    [BindProperty(SupportsGet = true)]
    public string Durum { get; set; } = "Tumu";

    public List<Row> Rows { get; set; } = [];
    public int CountTumu { get; set; }
    public int CountGonderildi { get; set; }
    public int CountHatali { get; set; }
    public int CountGonderilmedi { get; set; }

    [TempData]
    public string? ResultMessage { get; set; }

    public record Row(DoktorUsage Doktor, SyncLogEntry? DurumKaydi);

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    // "gönderirse bir daha hiçbir protokolde göndermesin" -- ama elle her zaman tekrar
    // gonderilebilir (KULLANICI ISTEGI: "hatalı olanı da denemesinde sıkıntı yok").
    public async Task<IActionResult> OnPostGonderAsync(int doktorId, CancellationToken ct)
    {
        var result = await practitionerSyncService.SyncOneAsync(doktorId, liveMode: true, ct);
        ResultMessage = result.Status == SyncStatus.Success
            ? $"{result.PatientFullName ?? doktorId.ToString()} gönderildi."
            : $"{result.PatientFullName ?? doktorId.ToString()} gönderilemedi -- {result.Message}";
        return RedirectToPage("/Doktorlar", new { Search, Durum });
    }

    public async Task<IActionResult> OnPostSilAsync(int doktorId, CancellationToken ct)
    {
        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", [doktorId], ct);
        var latest = statuses.GetValueOrDefault(doktorId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Doktorlar", new { Search, Durum });
    }

    // Toplu Gönder -- KULLANICI ISTEGI (2026-08-25): "Hasta Detay'daki gibi checkbox ve
    // gönder butonu olsun". Sil dahil edilmedi, sadece Gönder istendi. Zaten basariyla
    // gonderilmis olanlar (EncounterSyncService'teki cache kuraliyla AYNI mantik) tekrar
    // gonderilmez, atlanir -- bilerek tekrar gondermek isteyen tek tek "Tekrar Gönder"
    // butonunu kullanmaya devam eder.
    public async Task<IActionResult> OnPostBulkGonderAsync(List<int> selectedDoktorIds, CancellationToken ct)
    {
        var distinctIds = selectedDoktorIds.Distinct().ToList();
        var existingStatuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", distinctIds, ct);

        int ok = 0, failed = 0, alreadySent = 0;
        foreach (var doktorId in distinctIds)
        {
            if (existingStatuses.GetValueOrDefault(doktorId) is { Status: SyncStatus.Success, AzResourceId: not null })
            {
                alreadySent++;
                continue;
            }

            var result = await practitionerSyncService.SyncOneAsync(doktorId, liveMode: true, ct);
            if (result.Status == SyncStatus.Success) ok++; else failed++;
        }

        ResultMessage = distinctIds.Count == 0
            ? "Hiçbir doktor seçilmedi."
            : $"{distinctIds.Count} doktor işlendi -- {ok} gönderildi, {alreadySent} zaten gönderilmişti (atlandı), {failed} hata.";
        return RedirectToPage("/Doktorlar", new { Search, Durum });
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var usage = await pusulaRepository.GetUsedDoktorlarAsync(UsageWindowDays, ct);
        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            usage = usage
                .Where(d => d.AdiSoyadi.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || (d.TCKimlikNo?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Practitioner", usage.Select(d => d.DoktorId).ToList(), ct);
        var allRows = usage.Select(d => new Row(d, statuses.GetValueOrDefault(d.DoktorId))).ToList();

        CountTumu = allRows.Count;
        CountGonderildi = allRows.Count(r => r.DurumKaydi?.Status == SyncStatus.Success);
        CountHatali = allRows.Count(r => r.DurumKaydi?.Status == SyncStatus.Failed);
        CountGonderilmedi = CountTumu - CountGonderildi - CountHatali;

        Rows = Durum switch
        {
            "Gonderildi" => allRows.Where(r => r.DurumKaydi?.Status == SyncStatus.Success).ToList(),
            "Hatali" => allRows.Where(r => r.DurumKaydi?.Status == SyncStatus.Failed).ToList(),
            "Gonderilmedi" => allRows.Where(r => r.DurumKaydi is null).ToList(),
            _ => allRows,
        };
    }
}
