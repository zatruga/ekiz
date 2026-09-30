using System.Text.Json.Nodes;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Sync;

// IPTAL SENKRONU (Is 3) -- Pusula'da iptal edilmis olan ama e-Health'e GONDERILMIS
// kayitlari geri alir.
//
// DIGER SENKRONLARIN TERSI YONDE CALISIR: onlar "Pusula'da var, bizde yok" arar; bu
// "bizde var, Pusula'da artik gecersiz" arar. Bu yuzden ayri bir servis.
//
// KULLANICI KARARI (2026-09-09): silme OTOMATIK yapilir, onay adimi yok ("yanlislikla
// silme durumu olmayacaktir, silinmisse silinmis kabul edeceğiz"). Uyari kayda gecirildi:
// gonderim idempotenttir (yanlissa tekrar gonderilir) ama silme devlet kayit sisteminden
// KALICI veri cikarir.
//
// SILME SIRASI DISTAN ICERIYE: FHIR, hala referans edilen bir kaydi silmeyi HTTP 409 ile
// reddeder. Referans veren once gider:
//     DiagnosticReport (rapor) -> Composition -> Observation -> Procedure/Condition -> Encounter
// Patoloji zincirinin kendi ic sirasi DeleteService'te zaten kodlu (DiagnosticReport-Patoloji
// silinince Composition-Patoloji ve Observation-Patoloji de dogru sirada gider).
//
// SADECE GONDERDIKLERIMIZ: hic gonderilmemis bir kaydin iptali bizim icin olaysizdir --
// SyncLog'da Success + AzResourceId yoksa dokunulmaz.
public class CancellationSyncService(
    PusulaRepository repository,
    SyncLogStore syncLog,
    SettingsStore settings,
    DeleteService deleteService,
    EHealthClient eHealthClient,
    ILogger<CancellationSyncService> logger)
{
    // Pencere bekleyen is taramasiyla AYNI kaynaktan geliyor (Ayarlar) -- ikisi ayri
    // cozulurse "gonderilecekler" ile "iptal edilecekler" farkli araliklara bakardi.
    public async Task<CancellationRunResult> RunAsync(
        int? scanDays = null, CancellationToken ct = default)
    {
        var fromLocal = scanDays is { } gun && gun > 0
            ? DateTime.Now.Date.AddDays(-gun)
            : await PendingWorkService.TaramaBaslangiciAsync(settings, ct);
        int silinen = 0, hata = 0;

        // 1) IPTAL EDILMIS PROTOKOLLER -- butun zincir gecersiz.
        var iptalProtokoller = await repository.GetCancelledProtokollerAsync(fromLocal, ct);
        foreach (var p in iptalProtokoller)
        {
            var (ok, err) = await DeleteProtocolChainAsync(p.ProtokolId, ct: ct);
            silinen += ok; hata += err;
        }
        var iptalProtokolIdSet = iptalProtokoller.Select(p => p.ProtokolId).ToHashSet();

        // 2) IPTAL EDILMIS TEK ISLEMLER -- protokolu ayakta ama islem iptal edilmis.
        //    Protokolu zaten iptal olanlar 1. adimda toptan halledildi, tekrar denenmez.
        var iptalIslemler = await repository.GetCancelledProceduresAsync(fromLocal, ct);
        foreach (var i in iptalIslemler)
        {
            if (iptalProtokolIdSet.Contains(i.ProtokolId)) continue;
            var (ok, err) = await DeleteProcedureWithDependentsAsync(i.ProtokolId, i.PusulaId, ct);
            silinen += ok; hata += err;
        }

        // 3) IPTAL EDILMIS RADYOLOJI RAPORLARI (2026-09-29'da eklendi).
        //    Bir radyoloji raporu, bagli oldugu islem iptal edilmeden de tek basina
        //    gecersiz kilinabiliyor (onay iptali / rapor-yazildi iptali). Boyle bir
        //    sorgu hic yoktu, yani bu raporlar e-Health'te asili kaliyordu.
        var iptalRadyoloji = await repository.GetCancelledRadiologyAsync(fromLocal, ct);
        foreach (var r in iptalRadyoloji)
        {
            if (iptalProtokolIdSet.Contains(r.ProtokolId)) continue;
            var (ok, err) = await DeleteTargetsAsync([("DiagnosticReport", r.PusulaId)], "iptal radyoloji", ct);
            silinen += ok; hata += err;
        }

        // 4) TERS YONLU KONTROL -- "gonderdiklerim Pusula'da hala gecerli mi?"
        var (tersSilinen, tersHata) = await TersKontrolAsync(ct);
        silinen += tersSilinen; hata += tersHata;

        return new CancellationRunResult(
            iptalProtokoller.Count, iptalIslemler.Count, iptalRadyoloji.Count, silinen, hata);
    }

    // TERS YONLU KONTROL (2026-09-29, kullanici istegi uzerine patoloji/epikriz/islem/
    // muayene de kapsandi).
    //
    // NEDEN TARAMA YETMIYOR -- her kaynakta ayri bir sebep, hepsi olculdu:
    //   Laboratuvar : kaynak view onceden Status=6 ile filtreli, iptal edilen satir
    //                 view'dan TAMAMEN kayboluyor (7 gunluk pencerede donen 52.850
    //                 satirin tamami State=6). Taranacak bir "iptal" satiri yok.
    //   Epikriz     : kilidi acilan epikrizin iptal izi yok, sadece KilitDurumuId degisiyor.
    //   Islem       : State=0 olan 22 kayitta IptalTarihi BOS -- tarih filtresi atliyor.
    //   Patoloji    : bugun canli veride ornegi yok (State=0 ve ReportState<>4 icin 0
    //                 kayit olculdu) ama kolonlar var, yani yarin olabilir.
    //
    // Her biri icin ayri tarih sorgusu yazmak yerine soru tersine cevriliyor: gonderdigimiz
    // id'leri Pusula'ya verip "hangileri hala gecerli" diye soruyoruz, donmeyen iptal
    // sayiliyor. Kosullar gonderim kosullarinin BIREBIR AYNISI (bkz. TersKontrolKaynagi) --
    // farkli olsalardi sildigimizi bir sonraki turda yeniden gonderirdik.
    //
    // SIRA DISTAN ICERIYE: FHIR hala referans edilen bir kaydi 409 ile reddeder, bu yuzden
    // raporlar once, Procedure en sonda.
    private static readonly string[] TersKontrolSirasi =
        ["DiagnosticReport-Patoloji", "DiagnosticReport", "Observation", "Composition", "Procedure"];

    // TOPLU YANLIS SILME KORUMASI. Silme bakanlik sisteminden KALICI veri cikarir, bu
    // yuzden iki kapi var:
    //   1. Sorgu hic satir dondurmediyse HICBIR SEY silinmez -- "gonderdigimin hepsi iptal
    //      edilmis" gercek hayatta olmaz, cok daha buyuk ihtimalle sorgu/baglanti sorunudur.
    //   2. Kayip oran esigi asarsa durdurulur ve hata loglanir.
    private const double KayipOraniEsigi = 0.20;   // %20
    private const int KayipTabani = 20;            // bu sayinin altinda oran kapisi islemez

    private async Task<(int Silinen, int Hata)> TersKontrolAsync(CancellationToken ct)
    {
        int silinen = 0, hata = 0;

        foreach (var resourceType in TersKontrolSirasi)
        {
            var gonderilmis = await syncLog.GetLiveSentIdsAsync(resourceType, ct);
            if (gonderilmis.Count == 0) continue;

            var halaVar = await repository.GetExistingIdsAsync(resourceType, gonderilmis, ct);

            if (halaVar.Count == 0)
            {
                logger.LogError(
                    "Ters kontrol DURDURULDU ({Tip}): gonderilmis {Adet} kaydin HICBIRI Pusula'da "
                    + "bulunamadi. Bu hepsinin iptal edildigi anlamina gelmez -- cok daha buyuk "
                    + "ihtimalle sorgu ya da baglanti sorunu. Silme yapilmadi.",
                    resourceType, gonderilmis.Count);
                hata++;
                continue;
            }

            var kayip = gonderilmis.Where(id => !halaVar.Contains(id)).ToList();
            if (kayip.Count == 0) continue;

            var oran = (double)kayip.Count / gonderilmis.Count;
            if (kayip.Count >= KayipTabani && oran > KayipOraniEsigi)
            {
                logger.LogError(
                    "Ters kontrol DURDURULDU ({Tip}): {Adet} kaydin {Kayip} tanesi ({Oran:P1}) "
                    + "Pusula'da bulunamadi -- esik %{Esik}. Bu kadar coklu iptal beklenmez, "
                    + "veri/sorgu sorunu varsayildi. Silme yapilmadi.",
                    resourceType, gonderilmis.Count, kayip.Count, oran, KayipOraniEsigi * 100);
                hata++;
                continue;
            }

            foreach (var id in kayip)
            {
                var (ok, err) = await DeleteTargetsAsync([(resourceType, id)], "ters kontrol", ct);
                silinen += ok; hata += err;
            }
            logger.LogInformation(
                "Ters kontrol ({Tip}): {Adet} gonderilmis kayit kontrol edildi, {Kayip} tanesi "
                + "Pusula'da gecerli degil -> {Silinen} kayit silindi.",
                resourceType, gonderilmis.Count, kayip.Count, silinen);
        }

        return (silinen, hata);
    }

    // Bir protokolun e-Health'teki HER kaydini distan iceriye siler. Cocuk kayitlarin
    // PusulaId'leri SyncLog'da protokole bagli tutulmadigi icin (her tip kendi id uzayini
    // kullanir) once Pusula'dan yeniden okunur.
    // PUBLIC (2026-09-14): Protokol Detay'daki "Tümünü Sil" dugmesi de AYNI metodu
    // cagiriyor. Silme sirasi (distan ice) ve "Patient bilerek silinmez" karari tek
    // bir yerde kalsin diye sayfada kopyalanmadi -- iki kopya kacinilmaz olarak
    // birbirinden ayrisir ve o an FHIR 409 (hala referans veriliyor) olarak patlar.
    public async Task<(int Ok, int Err)> DeleteProtocolChainAsync(
        int protokolId, string? baglam = null, CancellationToken ct = default)
    {
        var hedefler = new List<(string ResourceType, int PusulaId)>();

        // -- En distaki halka: raporlar --
        foreach (var r in await repository.GetPathologyReportsByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("DiagnosticReport-Patoloji", r.ResultId));
        foreach (var r in await repository.GetRadiologyReportsByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("DiagnosticReport", r.TetkikIslemId));
        foreach (var l in await repository.GetLabResultsByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("Observation", l.LabaratuarSonucId));

        // VITAL BULGULAR (2026-09-30'da eklendi -- tani sorunu arastirilirken bulundu).
        // Zincirde HIC YOKTULAR: "Tümünü Sil" sonrasi bakanlikta yetim kaliyorlardi, ustelik
        // az-observation govdesi Encounter'a referans verdigi icin (VitalSignsMapper) en
        // sondaki Encounter silmesini de HTTP 409 ile dusuruyorlardi. Laboratuvardan farkli
        // bir anahtar kullaniyorlar: ResourceType "Observation-Vital", PusulaId =
        // GenelMuayene.Id (bkz. VitalSignsSyncService) -- bu yuzden lab dongusune takilmiyor.
        if (await repository.GetGenelMuayeneByProtokolIdAsync(protokolId, ct) is { } muayene)
            hedefler.Add(("Observation-Vital", muayene.Id));

        // -- Ortadaki halka: epikriz, islemler --
        hedefler.Add(("Composition", protokolId));
        foreach (var i in await repository.GetIslemlerByProtokolIdAsync(protokolId, ct))
            hedefler.Add(("Procedure", i.Id));

        baglam ??= $"iptal protokol {protokolId}";
        var (ok, err) = await DeleteTargetsAsync(hedefler, baglam, ct);

        // -- Tanilar AYRI bir asama (2026-09-30): Encounter.diagnosis baglantisi once
        //    cozulmeli, yoksa sunucu HTTP 409 ile reddeder ve tani bakanlikta yetim kalir
        //    (bkz. EncounterTanilariniAyirAsync). Cozme basarisiz olsa bile silme YINE
        //    deneniyor -- eski davranisin aynisi, yani bu adim hicbir durumu kotulestirmiyor.
        var tanilar = await repository.GetTanilarByProtokolIdAsync(protokolId, ct);
        if (tanilar.Count > 0)
        {
            await TanilariEncounterdanCozAsync(protokolId, tanilar.Select(t => t.Id).ToList(), ct);
            var (taniOk, taniErr) = await DeleteTargetsAsync(
                tanilar.Select(t => ("Condition", t.Id)).ToList(), baglam, ct);
            ok += taniOk; err += taniErr;
        }

        // -- En icteki halka: Muayine. EN SON silinir, cunku yukaridakilerin hepsi ona
        //    referans veriyor. (Patient BILEREK silinmez: baska protokollerde de kullaniliyor.)
        var (encOk, encErr) = await DeleteTargetsAsync([("Encounter", protokolId)], baglam, ct);
        return (ok + encOk, err + encErr);
    }

    // Tek bir islem iptal edildiginde: o isleme BAGLI raporlar once silinmeli, cunku
    // DiagnosticReport.extension:related-procedure ile Procedure'a referans veriyorlar --
    // once Procedure'i silmeye calisirsak HTTP 409 alir.
    private async Task<(int Ok, int Err)> DeleteProcedureWithDependentsAsync(int protokolId, int islemId, CancellationToken ct)
    {
        var hedefler = new List<(string, int)>();

        foreach (var r in await repository.GetPathologyReportsByProtokolIdAsync(protokolId, ct))
            if (r.ProtokolIslemId == islemId) hedefler.Add(("DiagnosticReport-Patoloji", r.ResultId));
        foreach (var r in await repository.GetRadiologyReportsByProtokolIdAsync(protokolId, ct))
            if (r.ProtokolIslemId == islemId) hedefler.Add(("DiagnosticReport", r.TetkikIslemId));

        hedefler.Add(("Procedure", islemId));

        return await DeleteTargetsAsync(hedefler, $"iptal işlem {islemId}", ct);
    }


    // TANI SILINEMIYOR (2026-09-30, kullanici sunucuda bildirdi: "tümünü sil denildiğinde
    // tanı silinememektedir").
    //
    // Encounter ile Condition BIRBIRINE referans veriyor:
    //     Encounter.diagnosis[].condition -> Condition/x   (bakanlik istegi, 2026-08-24)
    //     Condition.encounter             -> Encounter/y   (profilde zorunlu)
    //
    // Sunucu referans butunlugunu zorluyor -- canli kayitlarda dogrulandi:
    // "HTTP 409: There are other resources referencing this resource". Yani hangisini once
    // silmeye kalkarsak kalkalim reddediliyor; bu bir SIRALAMA sorunu degil, cift yonlu bir
    // halka. Dosyanin basindaki "distan iceriye sil" kurali tek yonlu zincirler icin dogru
    // ama boyle bir halkayi sirayla cozmek MUMKUN DEGIL.
    //
    // Eski davranisin sonucu: tanilar HIC silinemiyor, Encounter siliniyor ve tanilar
    // bakanlikta YETIM kaliyordu -- kullanici "sildim" sanarak. Silinmis bir protokolun
    // tanisinin devlet sisteminde asili kalmasi, silmenin amacini tamamen bosa cikariyor.
    //
    // COZUM gonderimin simetrigi. Gonderirken: Encounter yazilir -> Condition'lar yazilir ->
    // Encounter diagnosis ile GUNCELLENIR (EncounterSyncService.SyncDiagnosesAsync).
    // Silerken ters yonde: once Encounter'daki diagnosis baglantisi KALDIRILIR, boylece
    // Condition'i referans eden kimse kalmaz ve silinebilir; ardindan Encounter da gider.
    //
    // GOVDE BASTAN URETILMIYOR: canli kaynak GET edilip yalnizca ilgili diagnosis ogeleri
    // cikariliyor. Eslestirme mantigi (bolum kodu, doktor, gelis tipi) gonderimden bu yana
    // degismis olabilir -- bir SILME islemi Encounter'in baska alanlarini sessizce
    // degistirmemeli.
    //
    // SECICI: yalnizca verilen Condition id'leri cikarilir. Tek bir tani silinirken
    // digerlerinin Encounter baglantisi korunur.
    public async Task<bool> EncounterTanilariniAyirAsync(
        int protokolId, IReadOnlyCollection<string> silinecekConditionIdleri, CancellationToken ct = default)
    {
        if (silinecekConditionIdleri.Count == 0) return true;

        var kayitlar = await syncLog.GetLatestByPusulaIdsAsync("Encounter", [protokolId], ct);
        if (kayitlar.GetValueOrDefault(protokolId) is not { AzResourceId: not null } encEntry) return true;
        // Encounter zaten silinmisse ya da hic basariyla yazilmamissa cozulecek bag yok.
        if (encEntry.Status != SyncStatus.Success || encEntry.Operation == SyncOperation.Delete) return true;

        var azEncounterId = encEntry.AzResourceId!;

        var okuma = await eHealthClient.GetAsync("Encounter", azEncounterId, ct);
        if (!okuma.Success || okuma.Body is null)
        {
            logger.LogWarning("Tani baglantisi cozulemedi: Encounter/{AzId} okunamadi (HTTP {Kod}).",
                azEncounterId, okuma.StatusCode);
            return false;
        }

        JsonObject? encounter;
        try { encounter = JsonNode.Parse(okuma.Body) as JsonObject; }
        catch (System.Text.Json.JsonException) { encounter = null; }
        if (encounter is null)
        {
            logger.LogWarning("Tani baglantisi cozulemedi: Encounter/{AzId} yaniti ayristirilamadi.", azEncounterId);
            return false;
        }

        var cikarilan = DiagnosisCikar(encounter, silinecekConditionIdleri);
        if (cikarilan == 0) return true;

        var sonuc = await eHealthClient.UpdateAsync("Encounter", azEncounterId, encounter, ct);

        // Aktivite Akisi'nda gorunsun: bu, bakanlik kaydinda gercek bir degisiklik.
        var kayit = new SyncLogEntry
        {
            ResourceType = "Encounter",
            PusulaId = protokolId,
            Status = sonuc.Success ? SyncStatus.Success : SyncStatus.Failed,
            Operation = SyncOperation.Update,
            AzResourceId = azEncounterId,
            PatientFullName = encEntry.PatientFullName,
            FathersName = encEntry.FathersName,
            BirthDate = encEntry.BirthDate,
            Gender = encEntry.Gender,
            Fin = encEntry.Fin,
            RecordOpenedAt = encEntry.RecordOpenedAt,
            Message = sonuc.Success
                ? $"{cikarilan} tanı bağlantısı kaldırıldı (silme öncesi çözme)"
                : EHealthErrorFormatter.Describe(sonuc.StatusCode ?? 0, sonuc.Body),
            RequestJson = encounter.ToJsonString(JsonDefaults.Options),
            ResponseJson = sonuc.Body,
        };
        await syncLog.InsertAsync(kayit, ct);

        if (!sonuc.Success)
            logger.LogWarning("Tani baglantisi cozulemedi: Encounter/{AzId} guncellenemedi (HTTP {Kod}).",
                azEncounterId, sonuc.StatusCode);

        return sonuc.Success;
    }


    // Encounter govdesindeki diagnosis ogelerinden, verilen Condition id'lerine isaret
    // edenleri CIKARIR; kac tane cikardigini doner. Govdeyi YERINDE degistirir.
    //
    // AYRI VE public: bir SILME kararinin dayandigi tek mantik parcasi bu. Ag cagrilarindan
    // bagimsiz oldugu icin canli sisteme hic dokunmadan dogrulanabiliyor -- referansi
    // yanlis eslestiren bir hata, silinmemesi gereken tanilari Encounter'dan koparirdi.
    public static int DiagnosisCikar(JsonObject encounter, IReadOnlyCollection<string> silinecekConditionIdleri)
    {
        if (silinecekConditionIdleri.Count == 0) return 0;
        if (encounter["diagnosis"] is not JsonArray diagnosis || diagnosis.Count == 0) return 0;

        var hedefler = silinecekConditionIdleri.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cikarilan = 0;
        for (var i = diagnosis.Count - 1; i >= 0; i--)
        {
            // "Condition/01a0..." -> "01a0...". Mutlak URL gelse de (FHIR bunu da kabul
            // eder) son parca yine id oldugu icin ayni kural calisiyor.
            var referans = diagnosis[i]?["condition"]?["reference"]?.AsValue();
            if (referans is null || !referans.TryGetValue<string>(out var yol) || yol is null) continue;
            var conditionId = yol[(yol.LastIndexOf('/') + 1)..];
            if (!hedefler.Contains(conditionId)) continue;
            diagnosis.RemoveAt(i);
            cikarilan++;
        }

        // diagnosis 0..* -- bos dizi birakmak yerine alani tamamen cikarmak daha temiz.
        if (cikarilan > 0 && diagnosis.Count == 0) encounter.Remove("diagnosis");
        return cikarilan;
    }

    // Belirli tanilarin (Pusula tani id'leri) Encounter baglantisini cozer -- cagiran taraf
    // AZ id'lerini kendisi toplamak zorunda kalmasin diye SyncLog'dan buluyor.
    public async Task<bool> TanilariEncounterdanCozAsync(
        int protokolId, IReadOnlyCollection<int> taniIdleri, CancellationToken ct = default)
    {
        if (taniIdleri.Count == 0) return true;

        var durumlar = await syncLog.GetLatestByPusulaIdsAsync("Condition", taniIdleri.ToList(), ct);
        var azIdler = durumlar.Values
            .Where(d => d is { AzResourceId: not null } && d.Operation != SyncOperation.Delete)
            .Select(d => d.AzResourceId!)
            .ToList();

        return await EncounterTanilariniAyirAsync(protokolId, azIdler, ct);
    }

    private async Task<(int Ok, int Err)> DeleteTargetsAsync(
        List<(string ResourceType, int PusulaId)> hedefler, string baglam, CancellationToken ct)
    {
        int ok = 0, err = 0;
        foreach (var (resourceType, pusulaId) in hedefler)
        {
            var son = await syncLog.GetLatestByPusulaIdsAsync(resourceType, [pusulaId], ct);
            if (son.GetValueOrDefault(pusulaId) is not { AzResourceId: not null } entry) continue;

            // Zaten silinmisse tekrar denenmez -- aksi halde her turda 404 uretirdi.
            if (entry.Operation == SyncOperation.Delete && entry.Status == SyncStatus.Success) continue;
            if (entry.Status != SyncStatus.Success) continue;

            var sonuc = await deleteService.DeleteAsync(entry, ct);
            if (sonuc.Status == SyncStatus.Success) ok++;
            else
            {
                err++;
                logger.LogWarning("Iptal senkronu silemedi ({Baglam}): {ResourceType}/{AzId} -- {Message}",
                    baglam, resourceType, entry.AzResourceId, sonuc.Message);
            }
        }
        return (ok, err);
    }
}

public record CancellationRunResult(
    int IptalProtokol, int IptalIslem, int IptalRadyoloji, int Silinen, int Hata);
