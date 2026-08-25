using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

public class DetailModel(
    SyncLogStore syncLog,
    PusulaRepository pusulaRepository,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    PractitionerSyncService practitionerSyncService,
    CompositionSyncService compositionSyncService,
    DeleteService deleteService,
    EHealthClient eHealthClient) : PageModel
{
    public SyncLogEntry? Entry { get; set; }
    public string? PrettyRequest { get; set; }
    public string? PrettyResponse { get; set; }
    public string? ResendMessage { get; set; }

    // Kayit Detayi -> ilgili Protokol. Encounter/Composition icin PusulaId DOGRUDAN
    // ProtokolId'dir (birebir); Patient/Practitioner icin PusulaId bir Hasta/Doktor Id'si --
    // birden fazla protokolu olabilecegi icin EN SON protokol gosterilir (bkz.
    // PusulaRepository.GetMostRecentProtokolByHastaIdAsync/DoktorIdAsync, KULLANICI ISTEGI
    // 2026-08-21: "protokole dön butonu hepsinde olsun, hasta/protokol/bölüm adları da").
    //
    // DUZELTME (2026-08-21, canli olayda bulundu): "en son protokol" tahmini YANLIS
    // protokole goturebiliyordu -- kullanici Rauf'un protokolundeki bir Doktor kaydina
    // girip "Protokole dön" tikladiginda, o doktorun BASKA (daha yeni) bir protokolu varsa
    // oraya (baska bir HASTAYA) goturuluyordu. Cozum: Protokol.cshtml'deki "Detay" linkleri
    // artik hangi protokolden gelindigini fromProtokol route degeriyle ACIKCA tasiyor --
    // bu doluysa DOGRUDAN o protokol kullanilir (tahmin yok), sadece dolu degilse (orn.
    // Aktivite Akisi'ndan gelindiyse) eski "en son protokol" sezgisine dusulur.
    [BindProperty(SupportsGet = true)]
    public int? FromProtokol { get; set; }

    public ProtokolListItem? RelatedProtokol { get; set; }

    // "Gönderdiğim veri gerçekten doğru mu / doğru yere mi gitti" kontrolu -- kullanicinin
    // acikca istedigi bir dogrulama ekrani (bkz. konusma, 2026-08-20). ResponseJson sadece
    // o anki denemenin donen govdesini tutar; bu ise SORGU ANINDA bakanlikta GERCEKTE ne
    // var onu canli okur (aradan baska bir degisiklik gecmis olabilir). Otomatik degil,
    // "Canlı Kontrol Et" ile bilincli tetiklenir -- her sayfa acilisinda bakanliga bosuna
    // istek atilmasin diye.
    [BindProperty(SupportsGet = true)]
    public bool Verify { get; set; }

    public bool VerifyAttempted { get; set; }
    public bool VerifySuccess { get; set; }
    public int? VerifyStatusCode { get; set; }
    public string? PrettyLiveData { get; set; }

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        Entry = await syncLog.GetByIdAsync(id);
        if (Entry is null) return NotFound();
        PrettyRequest = Pretty(Entry.RequestJson);
        PrettyResponse = Pretty(Entry.ResponseJson);

        RelatedProtokol = FromProtokol is not null
            ? await pusulaRepository.GetProtokolByIdAsync(FromProtokol.Value, ct)
            : Entry.ResourceType switch
            {
                "Encounter" or "Composition" => await pusulaRepository.GetProtokolByIdAsync(Entry.PusulaId, ct),
                "Patient" => await pusulaRepository.GetMostRecentProtokolByHastaIdAsync(Entry.PusulaId, ct),
                "Practitioner" => await pusulaRepository.GetMostRecentProtokolByDoktorIdAsync(Entry.PusulaId, ct),
                _ => null,
            };

        if (Verify && Entry.AzResourceId is not null)
        {
            VerifyAttempted = true;
            var result = await eHealthClient.GetAsync(Entry.ResourceType, Entry.AzResourceId, ct);
            VerifySuccess = result.Success;
            VerifyStatusCode = result.StatusCode;
            PrettyLiveData = Pretty(result.Body);
        }

        return Page();
    }

    // "Tekrar gonder" -- Patient, Encounter, Practitioner, Composition (Epikriz) icin
    // destekleniyor (Lab/DiagnosticReport mapper'i henuz yazilmadi -- veri kaynagi
    // netlesmedi, bkz. SettingsStore.LabOnlyVerifiedKey yorumu).
    // KARAR (2026-08-20): artik CANLI gonderim yapar (liveMode:true) -- Encounter icin
    // hasta e-Health'te yoksa EncounterSyncService onu otomatik once canli gonderir.
    public async Task<IActionResult> OnPostResendAsync(long id)
    {
        var existing = await syncLog.GetByIdAsync(id);
        if (existing is null) return NotFound();

        switch (existing.ResourceType)
        {
            case "Patient":
                {
                    var result = await patientSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            case "Encounter":
                {
                    var result = await encounterSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            case "Practitioner":
                {
                    var result = await practitionerSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            case "Composition":
                {
                    var result = await compositionSyncService.SyncOneAsync(existing.PusulaId, liveMode: true);
                    return RedirectToPage("/Detail", new { id = result.Id });
                }
            default:
                ResendMessage = $"'{existing.ResourceType}' kayıt türü için tekrar gönderim henüz desteklenmiyor.";
                Entry = existing;
                PrettyRequest = Pretty(existing.RequestJson);
                PrettyResponse = Pretty(existing.ResponseJson);
                return Page();
        }
    }

    // Yanlislikla gonderilmis bir kaydi e-Health'ten geri almak icin -- sadece gercekten
    // olusturulmus/guncellenmis (AzResourceId dolu) kayitlar silinebilir.
    public async Task<IActionResult> OnPostDeleteAsync(long id)
    {
        var existing = await syncLog.GetByIdAsync(id);
        if (existing is null) return NotFound();

        var result = await deleteService.DeleteAsync(existing);
        return RedirectToPage("/Detail", new { id = result.Id });
    }

    private static string? Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            return node?.ToJsonString(PusulaEHealthSync.JsonDefaults.Indented);
        }
        catch
        {
            return json;
        }
    }
}
