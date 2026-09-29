using Microsoft.Data.SqlClient;

namespace PusulaEHealthSync.Db;

// Pusula'da "hazir" olan kayitlari PROTOKOL USTU (tek protokol degil, bir tarih araligindaki
// TUM protokoller icin) getiren sorgular. "Bekleyen Isler" ekrani ve saatlik dongu bunlarin
// sonucunu SyncLog ile karsilastirip farki alir -- bkz. PendingWorkService.
//
// NEDEN AYRI DOSYA: PusulaRepository zaten 1000+ satir; bu sorgular ayri bir amaca hizmet
// ediyor (toplu tarama), tek protokol sorgularinin yaninda kaybolmasinlar.
//
// TASARIM KARARI (2026-09-09, kullanici ile birlikte): her kaynak KENDI "sonuclanma tarihi"
// ile taranir, protokolun tarihiyle DEGIL. Sebep: 6 ay once acilmis bir protokolun patolojisi
// bugun onaylanabilir -- protokol tarihine gore tararsak bunu kaciririz. Kaynak bazinda
// dogru alanlar (hepsi canli veride %100 dolu olcuruldu, 2026-09-09):
//     Laboratuvar -> TetkikSonucOnayTarihi
//     Radyoloji   -> OnaylanmaTarihi
//     Patoloji    -> ApprovedDate
//     Prosedur    -> CreatedDate (Pusula'ya GIRILDIGI an; IslemTarihi ileri tarihli olabilir)
//     Epikriz     -> ModifiedDate (kilitlenme/duzeltme ile ilerliyor)
//
// TARIH SADECE TARAMAYI SINIRLAR, KARAR KRITERI DEGIL: asil kriter "SyncLog'da Success yok".
// Boylece dongu bir gun calismazsa kayit kaybolmaz, gonderilene kadar listede kalir.
public partial class PusulaRepository
{
    // Laboratuvar -- LabResultSyncService ile AYNI onay kurali (Status=6).
    // SyncLog karsiligi: ResourceType="Observation", PusulaId=LabaratuarSonucId.
    public async Task<List<PendingCandidate>> GetCompletedLabResultsAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT lab.VisitId, lab.LabaratuarSonucId, lab.TetkikSonucOnayTarihi, lab.TetkikAdi
            FROM LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId lab
            WHERE lab.Status = 6 AND lab.TetkikSonucOnayTarihi >= @From";
        return await QueryCandidatesAsync(sql, fromLocal, "Observation", "Laboratuvar", ct);
    }

    // Radyoloji -- RadiologyReportSyncService ile AYNI onay kurali (State=6).
    // SyncLog karsiligi: ResourceType="DiagnosticReport", PusulaId=TetkikIslem.Id.
    public async Task<List<PendingCandidate>> GetCompletedRadiologyAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT pi.ProtokolId, rti.Id, rti.OnaylanmaTarihi, oh.Adi
            FROM RIS.TetkikIslem rti
            INNER JOIN Hasta.ProtokolIslem pi ON pi.Id = rti.ProtokolIslemId
            INNER JOIN Ortak.Hizmet oh ON oh.Id = pi.HizmetId
            WHERE rti.State = 6 AND rti.OnaylanmaTarihi >= @From";
        return await QueryCandidatesAsync(sql, fromLocal, "DiagnosticReport", "Radyoloji", ct);
    }

    // Patoloji -- PathologyReportSyncService ile AYNI onay kurali (ReportState=4).
    // SyncLog karsiligi: ResourceType="DiagnosticReport-Patoloji", PusulaId=Result.Id.
    public async Task<List<PendingCandidate>> GetCompletedPathologyAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT r.VisitId, r.Id, r.ApprovedDate, r.Morphology
            FROM [EMR.Pathology].[Result] r
            WHERE r.ReportState = 4 AND r.ApprovedDate >= @From";
        return await QueryCandidatesAsync(sql, fromLocal, "DiagnosticReport-Patoloji", "Patoloji", ct);
    }

    // Prosedur -- GetIslemlerByProtokolIdAsync ile AYNI gecerlilik kurali (State>=2).
    // SyncLog karsiligi: ResourceType="Procedure", PusulaId=ProtokolIslem.Id.
    public async Task<List<PendingCandidate>> GetCreatedProceduresAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT pi.ProtokolId, pi.Id, pi.CreatedDate, oh.Adi
            FROM Hasta.ProtokolIslem pi
            INNER JOIN Ortak.Hizmet oh ON oh.Id = pi.HizmetId
            WHERE pi.State >= 2 AND pi.CreatedDate >= @From";
        return await QueryCandidatesAsync(sql, fromLocal, "Procedure", "İşlem", ct);
    }

    // Epikriz -- CompositionSyncService ile AYNI tamamlanma kurali (KilitDurumuId=1).
    // SyncLog karsiligi: ResourceType="Composition", PusulaId=ProtokolId.
    //
    // ModifiedDate KULLANILIYOR, EpikrizTamamlanmaTarihi DEGIL: ikincisi canli veride 66.463
    // kayitta 0 kere dolu (hic yazilmiyor). ModifiedDate ise EPIKRIZ METNI DOLU olan
    // kayitlarda %100 dolu (29.856/29.856, olculdu 2026-09-09) -- boş cikanlar zaten metni
    // olmadigi icin gonderilmeyen kayitlar. Ayrica epikriz SILINMEZ, DEGISIR: doktor metni
    // duzeltince ModifiedDate ilerler ve kayit yeniden listeye duser (Update olarak gider).
    public async Task<List<PendingCandidate>> GetLockedEpikrizAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT g.ProtokolId, g.ProtokolId, g.ModifiedDate, 'Epikriz'
            FROM Tedavi.GenelMuayene g
            WHERE g.KilitDurumuId = 1 AND g.State <> 0
              AND g.Epikriz IS NOT NULL AND DATALENGTH(g.Epikriz) > 0
              AND g.ModifiedDate >= @From";
        return await QueryCandidatesAsync(sql, fromLocal, "Composition", "Epikriz", ct);
    }

    // IPTAL EDILENLER (Is 3) -- gonderdiklerimizden artik gecerli olmayanlar. Diger
    // sorgularin TERSI yonde calisir: burada Pusula'da "yok olmus" olani ariyoruz.
    // State=0 + IptalTarihi canli veride %100 tutarli (32.400/32.400 olculdu, 2026-09-09).
    public async Task<List<PendingCandidate>> GetCancelledProceduresAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT pi.ProtokolId, pi.Id, pi.IptalTarihi, oh.Adi
            FROM Hasta.ProtokolIslem pi
            INNER JOIN Ortak.Hizmet oh ON oh.Id = pi.HizmetId
            WHERE pi.State = 0 AND pi.IptalTarihi >= @From";
        return await QueryCandidatesAsync(sql, fromLocal, "Procedure", "İptal edilen işlem", ct);
    }

    // Iptal edilmis PROTOKOLLER -- butun zincir (Encounter + altindaki her sey) gecersiz.
    //
    // DUZELTME (2026-09-29, kullanici sorusu uzerine bulundu): filtre yalnizca
    // ISNULL(KapanisTarihi, AcilisTarihi) >= @From idi -- yani KAYDIN tarihine bakiyordu,
    // IPTAL ANINA degil. 90 gun once kapanmis bir protokol bugun iptal edilse kapanis
    // tarihi pencerenin disinda kaldigi icin HIC fark edilmiyordu.
    //
    // hasta.protokol'de iptal tarihi tutan bir kolon YOK (olculdu: yalnizca ModifiedDate,
    // State, OldState, IptalAciklamasi var). ModifiedDate iptal aninin en iyi gostergesi
    // ve canli veride guvenilir: 10.898 iptal protokolun yalnizca 1'inde bos, 5'inde
    // kapanis tarihinden geride.
    //
    // Iki kosul OR ile birlestirildi -- yeni filtre eskisinin US KUMESI, yani bu degisiklik
    // daha once yakalanan hicbir kaydi kaybettiremez.
    public async Task<List<PendingCandidate>> GetCancelledProtokollerAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT p.Id, p.Id,
                   ISNULL(p.ModifiedDate, ISNULL(p.KapanisTarihi, p.AcilisTarihi)),
                   'İptal edilen protokol'
            FROM hasta.protokol p
            WHERE p.State = 0
              AND (p.ModifiedDate >= @From OR ISNULL(p.KapanisTarihi, p.AcilisTarihi) >= @From)";
        return await QueryCandidatesAsync(sql, fromLocal, "Encounter", "İptal edilen protokol", ct);
    }

    // Iptal edilmis RADYOLOJI tetkikleri (2026-09-29'da eklendi -- boyle bir sorgu HIC YOKTU).
    //
    // Radyoloji raporu, bagli oldugu islem iptal edilmeden de tek basina gecersiz
    // kilinabiliyor. RIS.TetkikIslem'de bunun UC ayri izi var ve ucu de canli veride dolu
    // (son 60 gun): OnayIptalTarihi 661, RaporYazildiIptalTarihi 640, IptalTarihi 0.
    // Hicbiri taranmadigi icin bu raporlar e-Health'te asili kaliyordu.
    //
    // State <> 6 SART: onayi iptal edilip SONRADAN yeniden onaylanmis bir tetkik tekrar
    // State=6 olur ve gonderilmeye devam etmeli -- onu silmek gercek veriyi kaybetmek olurdu.
    public async Task<List<PendingCandidate>> GetCancelledRadiologyAsync(DateTime fromLocal, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT rti.ProtokolId, rti.Id,
                   ISNULL(rti.IptalTarihi, ISNULL(rti.OnayIptalTarihi, rti.RaporYazildiIptalTarihi)),
                   'İptal edilen radyoloji raporu'
            FROM RIS.TetkikIslem rti
            WHERE rti.State <> 6
              AND (rti.IptalTarihi >= @From
                OR rti.OnayIptalTarihi >= @From
                OR rti.RaporYazildiIptalTarihi >= @From)";
        return await QueryCandidatesAsync(sql, fromLocal, "DiagnosticReport", "İptal edilen radyoloji raporu", ct);
    }

    // TERS YONLU KONTROL -- "gonderdigim laboratuvar sonuclari Pusula'da HALA duruyor mu?"
    //
    // NEDEN GEREKTI: laboratuvarda iptal, taranarak BULUNAMIYOR. Kaynak bir view
    // (LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId) ve onceden Status=6 ile
    // filtrelenmis -- olculdu: 7 gunluk pencerede donen 52.850 satirin TAMAMI State=6.
    // Yani iptal edilen satir view'dan tamamen KAYBOLUYOR; "iptal edildi" diye
    // isaretlenmis bir satir yok ki tarayalim.
    //
    // Cozum soruyu ters cevirmek: gonderdiklerimizin id listesini Pusula'ya verip
    // "bunlardan hangileri hala duruyor" diye soruyoruz. Donmeyen = iptal edilmis.
    //
    // Yalnizca HALA VAR OLANLARI dondurur; eksigi cagiran taraf hesaplar (bkz.
    // CancellationSyncService) -- boylece "sorgu hic satir dondurmedi" ile "hepsi
    // iptal edilmis" birbirine karismaz.
    public async Task<HashSet<int>> GetExistingLabResultIdsAsync(
        IReadOnlyCollection<int> labSonucIds, CancellationToken ct = default)
    {
        var mevcut = new HashSet<int>();
        if (labSonucIds.Count == 0) return mevcut;

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);

        // SQL Server'in 2100 parametre siniri -- rahat bir paylada ilerliyoruz.
        foreach (var chunk in labSonucIds.Distinct().Chunk(1000))
        {
            var isimler = chunk.Select((_, i) => "@p" + i).ToList();
            var sql = $@"
                SELECT lab.LabaratuarSonucId
                FROM LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId lab
                WHERE lab.Status = 6 AND lab.LabaratuarSonucId IN ({string.Join(",", isimler)})";
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue("@p" + i, chunk[i]);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) mevcut.Add(reader.GetInt32(0));
        }
        return mevcut;
    }

    // Aday listelerindeki protokolleri TOPLU getirir -- uygunluk kurallari (yatan/ayaktan,
    // 7 gun, 90 gun tavan) ve ekranda hasta/doktor gosterimi icin. Tek tek
    // GetProtokolByIdAsync cagirmak yuzlerce gidis-donus olurdu.
    public async Task<Dictionary<int, ProtokolListItem>> GetProtokollerByIdsAsync(
        IReadOnlyCollection<int> protokolIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, ProtokolListItem>();
        if (protokolIds.Count == 0) return result;

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);

        // IN listesi cok uzarsa SQL Server parametre sinirina (2100) takilir -- 1000'lik
        // partiler halinde okunuyor.
        foreach (var chunk in protokolIds.Distinct().Chunk(1000))
        {
            var names = string.Join(",", chunk.Select((_, i) => $"@p{i}"));
            var sql = $@"
                SELECT
                    p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                    p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                    p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State,
                    -- Yatan hastanin gercek taburcu ani. Bir protokolde birden fazla yatis
                    -- olabilir (servis degisimi vb.), en SON taburcu alinir.
                    (SELECT MAX(y.TaburcuTarihi) FROM Tedavi.Yatis y
                      WHERE y.ProtokolId = p.Id AND y.State <> 0) AS TaburcuTarihi
                FROM hasta.protokol p
                LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
                LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
                LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
                WHERE p.Id IN ({names})";

            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            for (var i = 0; i < chunk.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", chunk[i]);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var item = MapProtokol(reader);
                // MapProtokol ortak yardimci ve bu kolonu bilmiyor -- burada okunuyor.
                var ord = reader.GetOrdinal("TaburcuTarihi");
                item.TaburcuTarihi = reader.IsDBNull(ord) ? null : reader.GetDateTime(ord);
                result[item.ProtokolId] = item;
            }
        }
        return result;
    }

    private async Task<List<PendingCandidate>> QueryCandidatesAsync(
        string sql, DateTime fromLocal, string resourceType, string baslik, CancellationToken ct)
    {
        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        cmd.Parameters.AddWithValue("@From", fromLocal);

        var result = new List<PendingCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
            result.Add(new PendingCandidate
            {
                ProtokolId = reader.GetInt32(0),
                PusulaId = reader.GetInt32(1),
                ResourceType = resourceType,
                Baslik = baslik,
                // Pusula YEREL saat tutar -- SyncLog (UTC) ile karsilastirmadan once
                // AzTime.ToUtc'den gecirilmeli, bkz. PendingWorkService.
                SonuclanmaTarihi = reader.IsDBNull(2) ? DateTime.MinValue : reader.GetDateTime(2),
                Aciklama = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }
        return result;
    }
}

// Pusula'da hazir olan (ya da iptal edilmis) tek bir kalem. SyncLog ile karsilastirildiktan
// SONRA "bekliyor" ya da "gonderilmis" oldugu belli olur -- bu tip sadece adayi temsil eder.
public class PendingCandidate
{
    public int ProtokolId { get; set; }
    public int PusulaId { get; set; }
    public string ResourceType { get; set; } = "";
    public string Baslik { get; set; } = "";
    public DateTime SonuclanmaTarihi { get; set; }
    public string? Aciklama { get; set; }
}
