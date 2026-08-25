using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// Hasta Detay -- KULLANICI ISTEGI (2026-08-24): "protokol listesinde detay kismini
// ciftlemek gerek, hasta detay ve protokol detay olmali, hasta ozelinde protokollerini
// gorebilecegim ve islemler yapabilecegim bir panel olmali". Protokol Detay (bkz.
// Protokol.cshtml) TEK bir protokolun Hasta/Doktor/Muayine/Epikriz gonderim durumunu
// yonetir; bu sayfa ise bir hastanin TUM protokol GECMISINI tek ekranda gosterir --
// derin islemler (Muayine/Epikriz gonder-sil) YINE Protokol Detay'da kalir, tekrar
// yazilmaz -- burasi sadece genel bakis + Hasta (Patient) kaydinin kendisi icin
// gonder/sil (protokolden BAGIMSIZ, kisi bazli bir kaynak oldugu icin burada da anlamli).
public class HastaModel(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    CompositionSyncService compositionSyncService,
    DeleteService deleteService) : PageModel
{
    public HastaRecord? Hasta { get; set; }
    public List<ProtokolRow> Protokoller { get; set; } = [];
    public SyncLogEntry? HastaDurumKaydi { get; set; }

    [TempData]
    public string? BulkResultMessage { get; set; }

    public record ProtokolRow(ProtokolListItem Protokol, SyncLogEntry? MuayineDurumKaydi, SyncLogEntry? EpikrizDurumKaydi);

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Hasta = await pusulaRepository.GetHastaByIdAsync(id, ct);
        if (Hasta is null) return NotFound();

        await LoadAsync(id, ct);
        return Page();
    }

    // Hasta (Patient) kaydi -- protokolden bagimsiz, kisi bazli bir kaynak oldugu icin
    // burada da (Protokol Detay'a gitmeden) gonderilebilir/silinebilir olmasi mantikli.
    public async Task<IActionResult> OnPostGonderHastaAsync(int id, CancellationToken ct)
    {
        await patientSyncService.SyncOneAsync(id, liveMode: true, ct);
        return RedirectToPage("/Hasta", new { id });
    }

    public async Task<IActionResult> OnPostSilHastaAsync(int id, CancellationToken ct)
    {
        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [id], ct);
        var latest = statuses.GetValueOrDefault(id);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Hasta", new { id });
    }

    // Toplu Gönder/Sil -- KULLANICI ISTEGI (2026-08-24): "sol tarafa checkbox, listenin
    // üstüne sil ve gönder butonları, sil'e tıklayınca Tümünü/Müayine/Epikriz seçebileceğim
    // bir liste olsun". ResourceType secimi "Tumu" ise hem Encounter hem Composition
    // islenir -- Hasta (Patient) ve Doktor (Practitioner) bilerek DISINDA birakildi (Hasta
    // zaten bu sayfada ayri/kendi basina gonderiliyor; Doktor zaten Muayine/Epikriz
    // cascade'inde otomatik gonderiliyor, bkz. EncounterSyncService/CompositionSyncService).
    public async Task<IActionResult> OnPostBulkGonderAsync(int id, List<int> selectedProtokolIds, string resourceType, CancellationToken ct)
    {
        var distinctIds = selectedProtokolIds.Distinct().ToList();
        int ok = 0, skipped = 0, failed = 0;

        foreach (var protokolId in distinctIds)
        {
            if (resourceType is "Tumu" or "Encounter")
                Tally((await encounterSyncService.SyncOneAsync(protokolId, liveMode: true, ct)).Status, ref ok, ref skipped, ref failed);
            if (resourceType is "Tumu" or "Composition")
                Tally((await compositionSyncService.SyncOneAsync(protokolId, liveMode: true, ct)).Status, ref ok, ref skipped, ref failed);
        }

        BulkResultMessage = distinctIds.Count == 0
            ? "Hiçbir protokol seçilmedi."
            : $"{distinctIds.Count} protokol için gönderim denendi ({ResourceTypeLabel(resourceType)}) -- {ok} gönderildi, {skipped} atlandı, {failed} hata.";
        return RedirectToPage("/Hasta", new { id });
    }

    public async Task<IActionResult> OnPostBulkSilAsync(int id, List<int> selectedProtokolIds, string resourceType, CancellationToken ct)
    {
        var distinctIds = selectedProtokolIds.Distinct().ToList();
        int ok = 0, failed = 0, yok = 0;

        if (resourceType is "Tumu" or "Encounter")
            (ok, failed, yok) = Add((ok, failed, yok), await BulkSilResourceAsync("Encounter", distinctIds, ct));
        if (resourceType is "Tumu" or "Composition")
            (ok, failed, yok) = Add((ok, failed, yok), await BulkSilResourceAsync("Composition", distinctIds, ct));

        BulkResultMessage = distinctIds.Count == 0
            ? "Hiçbir protokol seçilmedi."
            : $"{distinctIds.Count} protokol için silme denendi ({ResourceTypeLabel(resourceType)}) -- {ok} silindi, {failed} hata, {yok} zaten e-Health'te kayıtlı değildi.";
        return RedirectToPage("/Hasta", new { id });
    }

    private static (int, int, int) Add((int ok, int failed, int yok) a, (int ok, int failed, int yok) b) =>
        (a.ok + b.ok, a.failed + b.failed, a.yok + b.yok);

    private async Task<(int ok, int failed, int yok)> BulkSilResourceAsync(string resourceType, List<int> protokolIds, CancellationToken ct)
    {
        var statuses = await syncLog.GetLatestByPusulaIdsAsync(resourceType, protokolIds, ct);
        int ok = 0, failed = 0, yok = 0;
        foreach (var protokolId in protokolIds)
        {
            var latest = statuses.GetValueOrDefault(protokolId);
            if (latest?.AzResourceId is null) { yok++; continue; }
            var result = await deleteService.DeleteAsync(latest, ct);
            if (result.Status == SyncStatus.Success) ok++; else failed++;
        }
        return (ok, failed, yok);
    }

    private static void Tally(SyncStatus status, ref int ok, ref int skipped, ref int failed)
    {
        switch (status)
        {
            case SyncStatus.Success: ok++; break;
            case SyncStatus.Skipped: skipped++; break;
            case SyncStatus.Failed: failed++; break;
        }
    }

    private static string ResourceTypeLabel(string resourceType) => resourceType switch
    {
        "Encounter" => "Müayinə",
        "Composition" => "Epikriz",
        _ => "Müayinə + Epikriz",
    };

    private async Task LoadAsync(int hastaId, CancellationToken ct)
    {
        var hastaStatuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [hastaId], ct);
        HastaDurumKaydi = hastaStatuses.GetValueOrDefault(hastaId);

        var protokoller = await pusulaRepository.GetProtokolsByHastaIdAsync(hastaId, ct);
        var protokolIds = protokoller.Select(p => p.ProtokolId).ToList();
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", protokolIds, ct);
        var compositionStatuses = await syncLog.GetLatestByPusulaIdsAsync("Composition", protokolIds, ct);

        Protokoller = protokoller
            .Select(p => new ProtokolRow(
                p,
                encounterStatuses.GetValueOrDefault(p.ProtokolId),
                compositionStatuses.GetValueOrDefault(p.ProtokolId)))
            .ToList();
    }
}
