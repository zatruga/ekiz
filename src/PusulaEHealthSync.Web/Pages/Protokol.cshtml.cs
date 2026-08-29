using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Mapping;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync.Web.Pages;

// KULLANICI ISTEGI (2026-08-24): Doktor (Practitioner) takibi/gonderimi bu sayfadan
// KALDIRILDI -- "protokol detayda olmasin, onun takibini yapmayalim, sistem tarafinda
// doktorlar diye ayri bir yer olsun". Doktor artik sadece EncounterSyncService'in
// otomatik cascade'i (bkz. o dosyadaki KARAR notu) ve /Doktorlar sayfasi uzerinden
// yonetiliyor.
public class ProtokolModel(
    PusulaRepository pusulaRepository,
    SyncLogStore syncLog,
    SettingsStore settings,
    PatientSyncService patientSyncService,
    EncounterSyncService encounterSyncService,
    CompositionSyncService compositionSyncService,
    ConditionSyncService conditionSyncService,
    ProcedureSyncService procedureSyncService,
    LabResultSyncService labResultSyncService,
    DeleteService deleteService,
    EHealthClient eHealthClient) : PageModel
{
    public ProtokolListItem? Protokol { get; set; }
    public HastaRecord? Hasta { get; set; }
    public GenelMuayeneRecord? GenelMuayene { get; set; }
    public SyncLogEntry? HastaDurumKaydi { get; set; }
    public SyncLogEntry? MuayineDurumKaydi { get; set; }
    public SyncLogEntry? EpikrizDurumKaydi { get; set; }
    public List<(IcdTaniRecord Tani, SyncLogEntry? Durum)> Tanilar { get; set; } = [];
    public List<(IslemRecord Islem, SyncLogEntry? Durum)> Islemler { get; set; } = [];

    // Laboratuvar (Tetkik) -- KULLANICI ISTEGI (2026-08-29): "tetkik kısmına başlayalım,
    // laboratuvar önce". GetLabResultsByProtokolIdAsync zaten sadece Status=6 (onaylanmis/
    // kesinlesmis) sonuclari donduruyor. procedure-code extension'i BILEREK eklenmedi (bkz.
    // LabResultObservationMapper) -- once canli $validate ile sunucunun bunu gercekten
    // reddedip reddetmedigi gorulecek (KULLANICI KARARI, 2026-08-29).
    public List<(LabResultRecord Lab, SyncLogEntry? Durum)> Labs { get; set; } = [];
    public bool LabsGonderilebilir => Labs.Any(l => !BasariylaGonderildi(l.Durum));
    public bool LabsSilinebilir => Labs.Any(l => SyncLogEntry.CanDelete(l.Durum));

    // Baslik satirindaki "Tümünü Sil"/"Tümünü Gönder" butonlarinin gorunurlugu -- KULLANICI
    // ISTEGI (2026-08-25): "tanı ve procedür başlıklarında tümünü sil ve eğer silinmiş ise
    // tümünü gönder butonu olmalı". Silinebilir olan (en az bir AzResourceId'li, henuz
    // silinmemis kayit) varsa Sil butonu; basariyla gonderilmemis (hic denenmemis, hatali
    // veya silinmis) en az bir kayit varsa Gönder butonu gosterilir.
    public bool TanilarSilinebilir => Tanilar.Any(t => SyncLogEntry.CanDelete(t.Durum));
    public bool TanilarGonderilebilir => Tanilar.Any(t => !BasariylaGonderildi(t.Durum));
    public bool IslemlerSilinebilir => Islemler.Any(i => SyncLogEntry.CanDelete(i.Durum));
    public bool IslemlerGonderilebilir => Islemler.Any(i => !BasariylaGonderildi(i.Durum));

    // KULLANICI ISTEGI (2026-08-25, dorduncu tur): "tanıyı sildim silinemedi yazısı
    // yazıyordu sonradan tekrar gönder dedim hata da kaldı ... böyle hiç bir zaman
    // kalmamalıyım bunu bir şekilde ya silmeli yada tekrar gönder yapabilmeliyim" -- eger
    // basarisiz bir deneme sonucunda AzResourceId null'a duserse (orn. Create de basarisiz
    // olursa) CanDelete de false donuyor, sadece TOPLU "Tümünü Gönder" kaliyor -- tekil
    // satirda ne Sil ne Detay ne de tekil Gönder vardi, kullanici hatanin SEBEBINI bile
    // goremiyordu. Artik public -- cshtml'de her satirda kullanılıyor.
    public static bool BasariylaGonderildi(SyncLogEntry? durum) =>
        durum is { Status: SyncStatus.Success } && durum.Operation != SyncOperation.Delete;
    public bool MuayineGonderimeUygun { get; set; }
    public int OpenProtokolSendAfterDays { get; set; }
    public bool EpikrizSendEnabled { get; set; }
    public bool EpikrizOnlySigned { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        Hasta = await pusulaRepository.GetHastaByIdAsync(Protokol.HastaId, ct);
        var hastaStatuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [Protokol.HastaId], ct);
        HastaDurumKaydi = hastaStatuses.GetValueOrDefault(Protokol.HastaId);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [Protokol.ProtokolId], ct);
        MuayineDurumKaydi = encounterStatuses.GetValueOrDefault(Protokol.ProtokolId);
        var compositionStatuses = await syncLog.GetLatestByPusulaIdsAsync("Composition", [Protokol.ProtokolId], ct);
        EpikrizDurumKaydi = compositionStatuses.GetValueOrDefault(Protokol.ProtokolId);

        // Tanı (Condition) ve İşlem (Procedure) -- KULLANICI ISTEGI (2026-08-25): "bu
        // yaptıklarımızı sistemimize ilave etmemişsin" -- ikisi de Müayinə cascade'iyle
        // otomatik gönderiliyordu ama Protokol Detay'da hiç görünmüyorlardı, sadece
        // Aktivite Akışı'ndan bulunabiliyorlardı. Ayrı bir "Gönder" butonu yok (cascade
        // zaten Müayinə "Gönder"ine bağlı), sadece son durum gösteriliyor.
        var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
        var taniStatuses = await syncLog.GetLatestByPusulaIdsAsync("Condition", tanilar.Select(t => t.Id).ToList(), ct);
        Tanilar = tanilar.Select(t => (t, taniStatuses.GetValueOrDefault(t.Id))).ToList();

        var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
        var islemStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", islemler.Select(i => i.Id).ToList(), ct);
        Islemler = islemler.Select(i => (i, islemStatuses.GetValueOrDefault(i.Id))).ToList();

        var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
        var labStatuses = await syncLog.GetLatestByPusulaIdsAsync("Observation", labs.Select(l => l.LabaratuarSonucId).ToList(), ct);
        Labs = labs.Select(l => (l, labStatuses.GetValueOrDefault(l.LabaratuarSonucId))).ToList();

        GenelMuayene = await pusulaRepository.GetGenelMuayeneByProtokolIdAsync(Protokol.ProtokolId, ct);
        EpikrizSendEnabled = await settings.GetBoolAsync(SettingsStore.EpikrizSendEnabledKey, true, ct);
        EpikrizOnlySigned = await settings.GetBoolAsync(SettingsStore.EpikrizOnlySignedKey, true, ct);

        OpenProtokolSendAfterDays = await settings.GetIntAsync(
            SettingsStore.OpenProtokolSendAfterDaysKey, SettingsStore.OpenProtokolSendAfterDaysDefault, ct);
        MuayineGonderimeUygun = EncounterMapper.IsEligibleForSend(Protokol.AcilisTarihi, Protokol.KapanisTarihi, OpenProtokolSendAfterDays);
        return Page();
    }

    // KARAR (2026-08-20, kullanici istegi): panelden "Gonder" artik CANLI gonderim yapar
    // (liveMode:true) -- eskiden sadece $validate calisiyordu. Muayine gonderiminde hasta
    // e-Health'te yoksa EncounterSyncService onu otomatik olarak once canli gonderir,
    // kullanicinin ayrica "once hastayi gonder" diye ugrasmasina gerek kalmaz.
    // Master "Tümünü Gönder" -- KULLANICI ISTEGI (2026-08-27): "e-Health gönderim durumu
    // alanının yanına tümünü gönder butonu koyalım" -- baslik satirinda tek tikla butun
    // protokolu gonderen bir kisayol. EncounterSyncService.SyncOneAsync zaten Hasta ->
    // Muayine -> Tani -> Islem'i CASCADE olarak gonderiyor (bkz. o dosyadaki SyncOneAsync),
    // burada ayrica tek tek cagirmaya gerek yok -- sadece cascade'e DAHIL OLMAYAN Epikriz'i
    // (Composition) ayrica gonderiyoruz. Reçete protokolleri e-Health'e hic gonderilmedigi
    // icin (sayfadaki diger butonlar gibi) bu durumda hicbir sey yapmiyor.
    public async Task<IActionResult> OnPostTumunuGonderAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        if (Protokol.ProtokolTipiId != EncounterMapper.ReceteProtokolTipiId)
        {
            await encounterSyncService.SyncOneAsync(Protokol.ProtokolId, liveMode: true, ct);
            await compositionSyncService.SyncOneAsync(Protokol.ProtokolId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderHastaAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await patientSyncService.SyncOneAsync(Protokol.HastaId, liveMode: true, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderMuayineAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await encounterSyncService.SyncOneAsync(Protokol.ProtokolId, liveMode: true, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Yanlislikla gonderilmis Hasta/Muayine kaydini e-Health'ten geri almak icin.
    public async Task<IActionResult> OnPostSilHastaAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [Protokol.HastaId], ct);
        var latest = statuses.GetValueOrDefault(Protokol.HastaId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilMuayineAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [Protokol.ProtokolId], ct);
        var latest = statuses.GetValueOrDefault(Protokol.ProtokolId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderEpikrizAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        await compositionSyncService.SyncOneAsync(Protokol.ProtokolId, liveMode: true, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilEpikrizAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var statuses = await syncLog.GetLatestByPusulaIdsAsync("Composition", [Protokol.ProtokolId], ct);
        var latest = statuses.GetValueOrDefault(Protokol.ProtokolId);
        if (latest is not null)
            await deleteService.DeleteAsync(latest, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tanı/İşlem satır bazlı silme -- KULLANICI ISTEGI (2026-08-25): "ama bence göndermeyi
    // otomatik yapabiliriz. fakat silme olmalı kesin." Otomatik gönderilenler (cascade)
    // yanlışlıkla/gereksiz gitmiş olabilir, bu yüzden her satırda tek tek silinebilmeli --
    // genel Detail sayfasındaki "Sil" zaten çalışıyordu ama buradan (Protokol Detay'dan)
    // tıklamak icin ayrı bir tur almaya gerek kalmasin diye dogrudan buraya da eklendi.
    public async Task<IActionResult> OnPostSilTaniAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostSilIslemAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tanı/İşlem satır bazlı (tekil) tekrar gönderim -- KULLANICI ISTEGI (2026-08-25):
    // "böyle hiç bir zaman kalmamalıyım bunu bir şekilde ya silmeli yada tekrar gönder
    // yapabilmeliyim" -- CanDelete false donduren (AzResourceId'i olmayan, basarisiz)
    // satirlar icin de tekil bir "Gönder" secenegi olsun diye -- eskiden sadece TOPLU
    // "Tümünü Gönder" vardi, tek bir kaydi hedefli sekilde tekrar denemek mumkun degildi.
    public async Task<IActionResult> OnPostGonderTaniAsync(int id, int taniId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
            var tani = tanilar.FirstOrDefault(t => t.Id == taniId);
            if (tani is not null)
                await conditionSyncService.SyncOneAsync(tani, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderIslemAsync(int id, int islemId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
            var islem = islemler.FirstOrDefault(i => i.Id == islemId);
            if (islem is not null)
                await procedureSyncService.SyncOneAsync(islem, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    // Tümünü Sil -- SADECE şu an başarıyla gönderilmiş (silinebilir) tanıları siler, tek
    // tek "Sil" ile birebir aynı DeleteService cagrisini her satir icin ayri ayri yapar --
    // KULLANICI ISTEGI (2026-08-25): "eğer listeden bir işlem yada tanı silinirse sadece o
    // silinmeli" -- yani bu bulk islem de aslinda birbirinden bagimsiz N tekil silme, ortak
    // bir "hepsini birden iptal et" cagrisi YOK, bu yuzden biri basarisiz olsa bile digerleri
    // etkilenmez.
    public async Task<IActionResult> OnPostTumunuSilTaniAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
        var taniStatuses = await syncLog.GetLatestByPusulaIdsAsync("Condition", tanilar.Select(t => t.Id).ToList(), ct);
        foreach (var durum in taniStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuSilIslemAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
        var islemStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", islemler.Select(i => i.Id).ToList(), ct);
        foreach (var durum in islemStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    // Tümünü Gönder -- SADECE henüz başarıyla gönderilmemiş (hiç denenmemiş/hatalı/silinmiş)
    // tanıları/işlemleri gönderir, halihazırda başarılı olanlara DOKUNMAZ -- ayni "sadece o
    // silinmeli, digerleri kalsin" mantiginin gonderim tarafindaki karsiligi. Encounter'in
    // gercek AZ id'si zaten bilinmiyorsa (Muayine hic gonderilmemisse) yapacak bir sey yok --
    // once Muayine "Gönder" ile gonderilmeli.
    public async Task<IActionResult> OnPostTumunuGonderTaniAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var tanilar = await pusulaRepository.GetTanilarByProtokolIdAsync(Protokol.ProtokolId, ct);
            var taniStatuses = await syncLog.GetLatestByPusulaIdsAsync("Condition", tanilar.Select(t => t.Id).ToList(), ct);
            foreach (var tani in tanilar)
            {
                if (BasariylaGonderildi(taniStatuses.GetValueOrDefault(tani.Id))) continue;
                await conditionSyncService.SyncOneAsync(tani, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuGonderIslemAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetGercekIdleriAsync(Protokol, ct);
        if (azPatientId is not null && azEncounterId is not null)
        {
            var islemler = await pusulaRepository.GetIslemlerByProtokolIdAsync(Protokol.ProtokolId, ct);
            var islemStatuses = await syncLog.GetLatestByPusulaIdsAsync("Procedure", islemler.Select(i => i.Id).ToList(), ct);
            foreach (var islem in islemler)
            {
                if (BasariylaGonderildi(islemStatuses.GetValueOrDefault(islem.Id))) continue;
                await procedureSyncService.SyncOneAsync(islem, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    // DUZELTME (2026-08-25, canli olayda bulundu): Kullanici bir Tanı'yı sildi (409 "hala
    // referans ediliyor" ile reddedildi -- Encounter.diagnosis hala isaret ediyordu), sonra
    // "Tekrar Gönder" dedi ve "HTTP 409: Non-existent reference: Encounter/..." hatasi aldi.
    // Kok neden: bizim SyncLogStore'daki "en son basarili Encounter" kaydi ESKI/GECERSIZ --
    // o Encounter e-Health sunucusunda ARTIK YOK (bizim tarafimizdan silinmedi, SyncLog'da
    // boyle bir Delete kaydi yok -- disaridan/sunucu tarafinda kaybolmus), ama biz hala o
    // ID'yi "gecerli" sanip Condition/Procedure'a referans olarak gonderiyorduk, sonsuza
    // kadar ayni 409'u alacak sekilde. Artik kullanmadan once GERCEKTEN var mi diye canli
    // kontrol ediliyor -- yoksa Muayine kendi kendini iyilestiriyor (EncounterSyncService
    // zaten FindExistingIdAsync ile canli arama yapip bulamazsa YENİ bir Encounter olusturur
    // ve cascade ile Tanı/İşlem'i de otomatik yeniden gonderir).
    public async Task<IActionResult> OnPostSilLabAsync(int id, long durumId, CancellationToken ct)
    {
        var entry = await syncLog.GetByIdAsync(durumId, ct);
        if (entry is not null)
            await deleteService.DeleteAsync(entry, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuSilLabAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
        var labStatuses = await syncLog.GetLatestByPusulaIdsAsync("Observation", labs.Select(l => l.LabaratuarSonucId).ToList(), ct);
        foreach (var durum in labStatuses.Values.Where(SyncLogEntry.CanDelete))
            await deleteService.DeleteAsync(durum, ct);
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostGonderLabAsync(int id, int labId, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(Protokol, ct);
        if (azPatientId is not null)
        {
            var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
            var lab = labs.FirstOrDefault(l => l.LabaratuarSonucId == labId);
            if (lab is not null)
                await labResultSyncService.SyncOneAsync(lab, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
        }
        return RedirectToPage("/Protokol", new { id });
    }

    public async Task<IActionResult> OnPostTumunuGonderLabAsync(int id, CancellationToken ct)
    {
        Protokol = await pusulaRepository.GetProtokolByIdAsync(id, ct);
        if (Protokol is null) return NotFound();

        var (azPatientId, azEncounterId) = await GetIdleriLabIcinAsync(Protokol, ct);
        if (azPatientId is not null)
        {
            var labs = await pusulaRepository.GetLabResultsByProtokolIdAsync(Protokol.ProtokolId, ct);
            var labStatuses = await syncLog.GetLatestByPusulaIdsAsync("Observation", labs.Select(l => l.LabaratuarSonucId).ToList(), ct);
            foreach (var lab in labs)
            {
                if (BasariylaGonderildi(labStatuses.GetValueOrDefault(lab.LabaratuarSonucId))) continue;
                await labResultSyncService.SyncOneAsync(lab, Protokol, azPatientId, azEncounterId, liveMode: true, ct);
            }
        }
        return RedirectToPage("/Protokol", new { id });
    }

    // Tanı/İşlem'deki GetGercekIdleriAsync'in aksine burada Muayine (Encounter) HENUZ
    // gonderilmemisse otomatik gondermeye ZORLAMIYORUZ -- Observation.encounter opsiyonel
    // (bkz. LabResultObservationMapper), lab sonucu tek basina (sadece Hasta'ya bagli olarak)
    // gonderilebilir. Kayitli Encounter id'si varsa GERCEKTEN gecerli mi diye canli kontrol
    // ediliyor (GetGercekIdleriAsync'teki ayni "409 riskini onle" mantigi) -- gecersizse
    // sadece referans eklenmiyor, yeni bir Encounter OLUSTURULMUYOR.
    private async Task<(string? AzPatientId, string? AzEncounterId)> GetIdleriLabIcinAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var patientStatuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [protokol.HastaId], ct);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId], ct);
        var azPatientId = patientStatuses.GetValueOrDefault(protokol.HastaId)?.AzResourceId;
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
            if (!check.Success)
                azEncounterId = null;
        }

        return (azPatientId, azEncounterId);
    }

    private async Task<(string? AzPatientId, string? AzEncounterId)> GetGercekIdleriAsync(ProtokolListItem protokol, CancellationToken ct)
    {
        var patientStatuses = await syncLog.GetLatestByPusulaIdsAsync("Patient", [protokol.HastaId], ct);
        var encounterStatuses = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokol.ProtokolId], ct);
        var azPatientId = patientStatuses.GetValueOrDefault(protokol.HastaId)?.AzResourceId;
        var azEncounterId = encounterStatuses.GetValueOrDefault(protokol.ProtokolId)?.AzResourceId;

        if (azEncounterId is not null)
        {
            var check = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
            if (!check.Success)
                azEncounterId = null; // kayitli id artik gecersiz -- asagida yeniden gonderilecek
        }

        if (azEncounterId is null)
        {
            var encResult = await encounterSyncService.SyncOneAsync(protokol.ProtokolId, liveMode: true, ct);
            azEncounterId = encResult.AzResourceId;
        }

        return (azPatientId, azEncounterId);
    }
}
