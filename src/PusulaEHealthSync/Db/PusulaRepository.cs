using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Config;
using PusulaEHealthSync.Persistence;

namespace PusulaEHealthSync.Db;

public class PusulaRepository(IOptions<PusulaOptions> options, SettingsStore settings)
{
    private readonly string _fallbackConnectionString = options.Value.ConnectionString;

    // KULLANICI ISTEGI (2026-08-28): "bu sistem pusulaya tamamen bağlı kalmasın ... db
    // bilgilerini yazdığımız bir ayar alanı bir panel olmalı" -- ileride farklı hastane
    // sistemlerine baglanabilme hedefinin ilk somut adimi. Baglanti dizesi artik
    // appsettings.Production.json'a gomulu SABIT bir deger degil, Ayarlar sayfasindan
    // girilebilen bir SettingsStore kaydi -- EHealthClient.OverrideOrAsync ile AYNI kalip
    // (Ayarlar'daki alan bos birakilirsa appsettings/user-secrets'taki degere duser), bu
    // yuzden hicbir sey degistirilmeden mevcut sunucu davranisi aynen calismaya devam eder.
    private async Task<string> ConnectionStringAsync(CancellationToken ct)
    {
        var stored = await settings.GetStringAsync(SettingsStore.PusulaConnectionStringKey, "", ct);
        return string.IsNullOrWhiteSpace(stored) ? _fallbackConnectionString : stored;
    }

    // Tek bir hastayi Pusula Id'sine gore okur -- ilk (Patient) senkronizasyon testleri icin.
    public async Task<HastaRecord?> GetHastaByIdAsync(int hastaId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT Id, Adi, Adi2, Soyadi, BabaAdi, DogumTarihi, CinsiyetId, AktifHastaId,
                   KanGrubuId, MedeniHaliId, GSM, SabitTel, Email, TCKimlikNo, CreatedDate,
                   IsBizdeDogan, AnneTCKimlikNo
            FROM hasta.hasta
            WHERE Id = @Id";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", hastaId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return Map(reader);
    }

    // Belirli bir tarihten sonra guncellenmis kucuk bir hasta partisi -- ileride "neyi
    // senkronize edecegiz" mantigi icin baslangic noktasi. Su an sadece TOP N ile sinirli,
    // gercek "son senkron zamani" takibi henuz yok (bir sonraki adim).
    public async Task<List<HastaRecord>> GetRecentHastalarAsync(int top, CancellationToken ct = default)
    {
        string sql = $@"
            SELECT TOP (@Top) Id, Adi, Adi2, Soyadi, BabaAdi, DogumTarihi, CinsiyetId, AktifHastaId,
                   KanGrubuId, MedeniHaliId, GSM, SabitTel, Email, TCKimlikNo, CreatedDate,
                   IsBizdeDogan, AnneTCKimlikNo
            FROM hasta.hasta
            ORDER BY ModifiedDate DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Top", top);

        var result = new List<HastaRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(Map(reader));
        return result;
    }

    // Protokol Listesi ana ekrani icin -- tarih araligi + serbest arama (hasta adi/FIN/
    // protokol Id) ile eslenen protokolleri, hasta/doktor/bolum bilgileriyle birlikte
    // getirir. Durum (senkron) filtresi SQLite'ta ayri tutuldugu icin burada uygulanmaz;
    // cagiran taraf (Web) donen adaylari SyncLogStore ile birlestirip filtreler.
    // KARAR (2026-08-20, kullanici istegi):
    //  - satir sinirlamasi (eskiden TOP 500) kaldirildi.
    //  - bir arama terimi (FIN/isim/protokol Id) girildiginde tarih araligi tamamen
    //    yok sayilir -- arama tum zamanlar icinde yapilir.
    //  - State=0 (iptal/silinmis) protokoller listeye hic girmiyor; Pusula'nin kendi
    //    gunluk raporlarindaki sayimla farkin sebebi buydu (bkz. konusma).
    public async Task<List<ProtokolListItem>> GetProtokolListAsync(
        DateTime fromDate, DateTime toDateExclusive, string? search, CancellationToken ct = default)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(search);
        var sql = @"
            SELECT
                p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State
            FROM hasta.protokol p
            LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.State <> 0";

        if (!hasSearch)
            sql += " AND p.AcilisTarihi >= @FromDate AND p.AcilisTarihi < @ToDateExclusive";
        else
        {
            sql += int.TryParse(search, out _)
                ? " AND (p.Id = @SearchInt OR h.TCKimlikNo = @Search)"
                : " AND (h.Adi LIKE @SearchLike OR h.Adi2 LIKE @SearchLike OR h.Soyadi LIKE @SearchLike OR h.TCKimlikNo LIKE @SearchLike)";
        }
        sql += " ORDER BY p.AcilisTarihi DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        if (!hasSearch)
        {
            cmd.Parameters.AddWithValue("@FromDate", fromDate);
            cmd.Parameters.AddWithValue("@ToDateExclusive", toDateExclusive);
        }
        else
        {
            if (int.TryParse(search, out var searchInt))
            {
                cmd.Parameters.AddWithValue("@SearchInt", searchInt);
                cmd.Parameters.AddWithValue("@Search", search);
            }
            else
            {
                cmd.Parameters.AddWithValue("@SearchLike", $"%{search}%");
            }
        }

        var result = new List<ProtokolListItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(MapProtokol(reader));
        return result;
    }

    public async Task<ProtokolListItem?> GetProtokolByIdAsync(int protokolId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State
            FROM hasta.protokol p
            LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.Id = @Id";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", protokolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapProtokol(reader) : null;
    }

    // Practitioner senkronu icin -- IK.Personel tek kayit okuma (Patient/GetHastaByIdAsync
    // ile ayni kalip).
    public async Task<PersonelRecord?> GetPersonelByIdAsync(int personelId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT Id, Adi, Soyadi, TCKimlikNo, CikisTarihi, PersonelTipiId
            FROM IK.Personel
            WHERE Id = @Id";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Id", personelId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new PersonelRecord
        {
            Id = reader.GetInt32(0),
            Adi = reader.IsDBNull(1) ? null : reader.GetString(1),
            Soyadi = reader.IsDBNull(2) ? null : reader.GetString(2),
            TCKimlikNo = reader.IsDBNull(3) ? null : reader.GetString(3),
            CikisTarihi = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
            PersonelTipiId = Convert.ToByte(reader.GetValue(5)),
        };
    }

    // Kayit Detayi sayfasinda "Protokole dön" butonu ve protokol/bolum bilgisi icin --
    // Patient/Practitioner kaydinin PusulaId'si (HastaId/DoktorId) DOGRUDAN bir protokol
    // degil (bir hastanin/doktorun birden fazla protokolu olabilir, KULLANICI ISTEGI,
    // 2026-08-21). Belirsizligi cozmek icin EN SON (AcilisTarihi'ne gore) protokol
    // gosteriliyor -- kullanicinin buraya genelde yakin zamanda gonderdigi bir kayittan
    // geldigi varsayimiyla en makul secim.
    public async Task<ProtokolListItem?> GetMostRecentProtokolByHastaIdAsync(int hastaId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP 1
                p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State
            FROM hasta.protokol p
            LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.HastaId = @HastaId
            ORDER BY p.AcilisTarihi DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HastaId", hastaId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapProtokol(reader) : null;
    }

    // Hasta Detay sayfasi icin -- bir hastanin TUM protokol gecmisi (KULLANICI ISTEGI,
    // 2026-08-24: "protokol listesinde detay kismini ciftlemek gerek -- hasta detay ve
    // protokol detay olmali, hasta ozelinde protokollerini gorebilecegim bir panel").
    // GetMostRecentProtokolByHastaIdAsync'in aksine TEK degil TUM protokolleri doner --
    // tarih araligi sinirlamasi yok (bir hastanin GECMISI genelde kisa bir pencereye
    // sigmaz), sadece State=0 (iptal/silinmis) protokoller listeden cikarilir (Protokol
    // Listesi'ndeki ayni kural).
    public async Task<List<ProtokolListItem>> GetProtokolsByHastaIdAsync(int hastaId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT
                p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State
            FROM hasta.protokol p
            LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.HastaId = @HastaId AND p.State <> 0
            ORDER BY p.AcilisTarihi DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@HastaId", hastaId);

        var result = new List<ProtokolListItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(MapProtokol(reader));
        return result;
    }

    public async Task<ProtokolListItem?> GetMostRecentProtokolByDoktorIdAsync(int doktorId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP 1
                p.Id AS ProtokolId, p.HastaId, h.Adi AS HastaAdi, h.Soyadi AS HastaSoyadi, h.TCKimlikNo AS Fin,
                p.DoktorId, pers.Adi AS DoktorAdi, pers.Soyadi AS DoktorSoyadi,
                p.BolumId, b.Adi AS BolumAdi, p.GelisTipiId, p.ProtokolTipiId, p.AcilisTarihi, p.KapanisTarihi, p.State
            FROM hasta.protokol p
            LEFT JOIN hasta.hasta h ON h.Id = p.HastaId
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.DoktorId = @DoktorId
            ORDER BY p.AcilisTarihi DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@DoktorId", doktorId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapProtokol(reader) : null;
    }

    // Epikriz (Composition) senkronu icin -- bir protokolun BIRDEN FAZLA GenelMuayene
    // kaydi olabilir (takip muayeneleri); epikriz genelde SONUNCUDA doldurulur. Bu yuzden
    // Epikriz dolu olan (varsa) en son degistirilen kaydi aliyoruz -- hicbiri dolu degilse
    // yine de en son kaydi doner (mapper zaten bos icerikte Skip edecek).
    public async Task<GenelMuayeneRecord?> GetGenelMuayeneByProtokolIdAsync(int protokolId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT TOP 1
                Id, ProtokolId, DoktorId, Epikriz, KilitDurumuId, EpikrizTamamlanmaTarihi,
                MuayeneBaslangicTarihi, MuayeneBitisTarihi, CreatedDate, ModifiedDate,
                Sikayeti, Tani, TaburcuPlani, Hikayesi, Soygecmisi, Bulgulari
            FROM Tedavi.GenelMuayene
            WHERE ProtokolId = @ProtokolId AND State <> 0
            ORDER BY CASE WHEN Epikriz IS NOT NULL AND LEN(Epikriz) > 0 THEN 1 ELSE 0 END DESC,
                     ISNULL(ModifiedDate, CreatedDate) DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProtokolId", protokolId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new GenelMuayeneRecord
        {
            Id = reader.GetInt32(0),
            ProtokolId = reader.GetInt32(1),
            DoktorId = reader.GetInt32(2),
            Epikriz = reader.IsDBNull(3) ? null : reader.GetString(3),
            KilitDurumuId = reader.IsDBNull(4) ? null : Convert.ToByte(reader.GetValue(4)),
            EpikrizTamamlanmaTarihi = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            MuayeneBaslangicTarihi = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            MuayeneBitisTarihi = reader.IsDBNull(7) ? null : reader.GetDateTime(7),
            CreatedDate = reader.GetDateTime(8),
            ModifiedDate = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
            Sikayeti = reader.IsDBNull(10) ? null : reader.GetString(10),
            Tani = reader.IsDBNull(11) ? null : reader.GetString(11),
            TaburcuPlani = reader.IsDBNull(12) ? null : reader.GetString(12),
            Hikayesi = reader.IsDBNull(13) ? null : reader.GetString(13),
            Soygecmisi = reader.IsDBNull(14) ? null : reader.GetString(14),
            Bulgulari = reader.IsDBNull(15) ? null : reader.GetString(15),
        };
    }

    // Laboratuvar (DiagnosticReport/Observation) senkronu icin -- LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId
    // baglı sunucudaki (COMED LIS) gercek verilere JOIN yaptigi icin AGIR bir view; filtresiz
    // COUNT(*) 30sn+ suruyor (canli denendi, 2026-08-21) -- VisitId ile filtrelenince hizli.
    // Status=6 (onaylanmis/kesinlesmis) filtresi burada, SQL tarafinda uygulaniyor -- sadece
    // Epikriz'deki KilitDurumuId=1 kuraliyla AYNI mantik: hala islemde olan sonuclar hic
    // donmez, C# tarafinda ayrica filtrelemeye gerek yok.
    public async Task<List<LabResultRecord>> GetLabResultsByProtokolIdAsync(int protokolId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT LabaratuarSonucId, VisitId, Status, TetkikAdi, TetkikSonucu, TetkikSonucuBirimi,
                   TetkikSonucuReferansDegeri, TetkikSonucuReferansDegerAraligindaMi, LoincKodu,
                   TetkikSonucTarihi, TetkikSonucOnayTarihi
            FROM LIS.uv_LaboratuarSonucKayitBilgileriByProtokolId
            WHERE VisitId = @ProtokolId AND Status = 6";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@ProtokolId", protokolId);

        var result = new List<LabResultRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new LabResultRecord
            {
                LabaratuarSonucId = reader.GetInt32(0),
                VisitId = reader.GetInt32(1),
                Status = reader.GetInt32(2),
                TetkikAdi = reader.IsDBNull(3) ? null : reader.GetString(3),
                TetkikSonucu = reader.IsDBNull(4) ? null : reader.GetString(4),
                TetkikSonucuBirimi = reader.IsDBNull(5) ? null : reader.GetString(5),
                TetkikSonucuReferansDegeri = reader.IsDBNull(6) ? null : reader.GetString(6),
                DisindaMi = !reader.IsDBNull(7) && reader.GetString(7) == "1",
                LoincKodu = reader.IsDBNull(8) ? null : reader.GetString(8),
                TetkikSonucTarihi = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                TetkikSonucOnayTarihi = reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            });
        }
        return result;
    }

    // Encounter.diagnosis (-> ayri Condition kaynaklari) icin -- KULLANICI ISTEGI
    // (2026-08-24, bakanlik geri bildirimi): "Encounter'a da epikriz/tani bilgisi
    // ekleyin". ic.Kodu NULL olan (ICDId eslesmesi bulunamayan, nadir) satirlar disarida
    // birakiliyor -- Condition.code (1..1 zorunlu) icin gecerli bir ICD-10 kodu sart.
    // Once BIRINCIL tani (varsa) gelir -- Encounter.diagnosis[].rank icin kullanilir.
    public async Task<List<IcdTaniRecord>> GetTanilarByProtokolIdAsync(int protokolId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT pi.Id, pi.ProtokolId, pi.ICDId, ic.Kodu, ic.Adi, pi.IsBirincilTani, pi.IsAnaTani
            FROM Tedavi.ProtokolICD pi
            JOIN Sube.Tedavi_ICD ic ON ic.Id = pi.ICDId
            WHERE pi.ProtokolId = @ProtokolId AND pi.State <> 0 AND ic.Kodu IS NOT NULL AND LEN(ic.Kodu) > 0
            ORDER BY CASE WHEN pi.IsBirincilTani = 1 THEN 0 ELSE 1 END, pi.Id";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProtokolId", protokolId);

        var result = new List<IcdTaniRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new IcdTaniRecord
            {
                Id = reader.GetInt32(0),
                ProtokolId = reader.GetInt32(1),
                ICDId = Convert.ToInt32(reader.GetValue(2)),
                Kodu = reader.GetString(3),
                Adi = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsBirincilTani = !reader.IsDBNull(5) && reader.GetBoolean(5),
                IsAnaTani = reader.IsDBNull(6) ? null : reader.GetBoolean(6),
            });
        }
        return result;
    }

    // AZ Procedure icin -- bir protokole uygulanmis, ICBARI SIGORTA FIYAT LISTESI
    // (KurumHizmetKategoriId=13) ile eslestirilmis hizmetler. KULLANICI KARARI (2026-08-25):
    // "ayrım için şuanda tek gönderim yapacağımız alan ICBARI SİGORTA FİYAT LİSTESİ
    // eşleştirilmesi yapılanları göndereceğiz" -- eslestirme statik bir Excel'den DEGIL,
    // Pusula'nin kendi Ortak.HizmetKurumHizmet/Pazarlama.KurumHizmet tablolarindan CANLI
    // okunuyor (kullanici: "eşleşmeler değişebilir, anlık günlük takip etmek gerekir").
    // Icbari Kodu formatinda bazen sonda nokta oluyor (orn. "1.1.11.") -- AZ'nin resmi
    // az-procedure-codes CodeSystem'i noktasiz (orn. "1.1.11"), bu yuzden cagiran taraf
    // (ProcedureMapper) TrimEnd('.') ile normalize ediyor.
    //
    // ONAY KONTROLU (2026-08-27, canli hata -- kullanici: "procedürlerde state kontrolü
    // yapmıyoruz, bekleyen/onaylanmamış hizmet gönderim listesinde olmamalı"): pi.State<>0
    // sadece istemin silinmedigini gosteriyor, ONAYLANDIGINI degil. Protokol 50729124 uzerinde
    // canli dogrulandi: pi.State=1 -> bekleyen (EKQ, henuz gonderilmemeli), pi.State=2 ->
    // onaylanmis/tamamlanmis (Transtorasik ekokardiyografi, gonderilmesi dogru). Bu yuzden
    // pi.State<>0 yerine pi.State=2 sartini kullaniyoruz.
    //
    // Kullanici ek uyari (2026-08-27): radyolojide pi.State=2 (protokol/fatura seviyesinde
    // onay) olsa BILE cekim yapilmamis ya da doktor RAPORU henuz onaylanmamis olabilir --
    // Pusula'nin kendi e-Nabiz entegrasyonu (bkz. docs/sql-exports/enabiz_procs.txt,
    // usp_GetProtokolIslemByENabiz, HizmetTipiId=3 dali) bu yuzden ayrica RIS.TetkikIslem.
    // State=6 ("onaylanmis/kesinlesmis", LIS tarafindaki Status=6 ile ayni kural) sartini da
    // ariyor. Ayni ek kontrolu burada da uyguluyoruz: bir ProtokolIslem'in silinmemis bir
    // RIS.TetkikIslem kaydi varsa ama o kayit henuz onaylanmamissa (State<>6), pi.State=2
    // olsa bile o Islem'i gonderim listesinden disarida birakiyoruz. RIS'e hic girmeyen
    // hizmetler (muayene, idari/faturalama kalemleri vb.) bu ek kontrolden etkilenmez --
    // onlarda zaten eslesen bir TetkikIslem kaydi olmuyor.
    //
    // Ayni risk laboratuvar tipi hizmetlerde de var (kullanici sordu, 2026-08-27): Pusula'nin
    // kendi export'unda (usp_GetLaboratuvarSonucBilgileriByEnabiz, satir 386) LIS.TestIslem.
    // State=6 RIS ile BIREBIR ayni "onaylanmis/kesinlesmis" kuralini, ayni sekilde
    // ProtokolIslemId uzerinden (isim degil, ID uzerinden) kullaniyor -- bu yuzden RIS
    // kontrolunun aynisini LIS.TestIslem icin de ekliyoruz.
    public async Task<List<IslemRecord>> GetIslemlerByProtokolIdAsync(int protokolId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT pi.Id, pi.ProtokolId, pi.HizmetId, oh.Adi AS HizmetAdi, pi.IslemTarihi,
                   icb.Kodu AS IcbariKodu, icb.Adi AS IcbariAdi
            FROM Hasta.ProtokolIslem pi
            INNER JOIN Ortak.Hizmet oh ON oh.Id = pi.HizmetId
            OUTER APPLY (
                SELECT TOP 1 PKH.Kodu, PKH.Adi
                FROM Ortak.HizmetKurumHizmet OHKH
                INNER JOIN Pazarlama.KurumHizmet PKH ON PKH.Id = OHKH.KurumHizmetId
                WHERE OHKH.HizmetId = oh.Id
                  AND PKH.KurumHizmetKategoriId = 13
                  AND PKH.State <> 0
                  AND OHKH.State <> 0
                ORDER BY PKH.IsPaket DESC
            ) icb
            WHERE pi.ProtokolId = @ProtokolId AND pi.State = 2 AND pi.HizmetId IS NOT NULL
              AND icb.Kodu IS NOT NULL AND LEN(icb.Kodu) > 0
              AND NOT EXISTS (
                  SELECT 1 FROM RIS.TetkikIslem rti
                  WHERE rti.ProtokolIslemId = pi.Id AND rti.State <> 0 AND rti.State <> 6
              )
              AND NOT EXISTS (
                  SELECT 1 FROM LIS.TestIslem lti
                  WHERE lti.ProtokolIslemId = pi.Id AND lti.State <> 0 AND lti.State <> 6
              )
            ORDER BY pi.Id";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@ProtokolId", protokolId);

        var result = new List<IslemRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new IslemRecord
            {
                Id = reader.GetInt32(0),
                ProtokolId = reader.GetInt32(1),
                HizmetId = reader.GetInt32(2),
                HizmetAdi = reader.IsDBNull(3) ? null : reader.GetString(3),
                IslemTarihi = reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                IcbariKodu = reader.GetString(5),
                IcbariAdi = reader.IsDBNull(6) ? reader.GetString(5) : reader.GetString(6),
            });
        }
        return result;
    }

    // Doktorlar ekrani icin -- KULLANICI ISTEGI (2026-08-24): "doktor takibini Protokol
    // Detay'dan kaldiralim, sistem tarafinda doktorlar diye ayri bir yer olsun". IK.Personel'in
    // tamami yerine son N gunde GERCEKTEN protokolu olan doktorlar, kullanim sikligina gore.
    public async Task<List<DoktorUsage>> GetUsedDoktorlarAsync(int days, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT p.DoktorId, pers.Adi, pers.Soyadi, pers.TCKimlikNo, COUNT(*) AS Adet
            FROM hasta.protokol p
            LEFT JOIN IK.Personel pers ON pers.Id = p.DoktorId
            WHERE p.AcilisTarihi >= @FromDate AND p.DoktorId IS NOT NULL
            GROUP BY p.DoktorId, pers.Adi, pers.Soyadi, pers.TCKimlikNo
            ORDER BY Adet DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@FromDate", DateTime.Now.AddDays(-days));

        var result = new List<DoktorUsage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new DoktorUsage
            {
                DoktorId = reader.GetInt32(0),
                Adi = reader.IsDBNull(1) ? null : reader.GetString(1),
                Soyadi = reader.IsDBNull(2) ? null : reader.GetString(2),
                TCKimlikNo = reader.IsDBNull(3) ? null : reader.GetString(3),
                Adet = reader.GetInt32(4),
            });
        }
        return result;
    }

    // Bolum Eslestirme ekrani icin -- Ortak.Bolum'un tamami (440 satir, cogu pasif/eski)
    // yerine son N gunde GERCEKTEN kullanilan bolumleri, kullanim sikligina gore doner.
    // KARAR (2026-08-20): otomatik isim-bazli eslestirme terk edildi, kullanici bunlari
    // elle AZ hospital-departments koduna eslestirecek (bkz. BolumMappingStore).
    public async Task<List<BolumUsage>> GetUsedDepartmentsAsync(int days, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT p.BolumId, b.Adi, COUNT(*) AS Adet
            FROM hasta.protokol p
            LEFT JOIN Ortak.Bolum b ON b.Id = p.BolumId
            WHERE p.AcilisTarihi >= @FromDate AND p.BolumId IS NOT NULL
            GROUP BY p.BolumId, b.Adi
            ORDER BY Adet DESC";

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@FromDate", DateTime.Now.AddDays(-days));

        var result = new List<BolumUsage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new BolumUsage
            {
                // BolumId SQL Server'da smallint -- GetInt32 direkt cast hatasi verir.
                BolumId = Convert.ToInt32(reader.GetValue(0)),
                Adi = reader.IsDBNull(1) ? null : reader.GetString(1),
                Adet = reader.GetInt32(2),
            });
        }
        return result;
    }

    // "Gonderilmis ama sonradan iptal/silinmis (State=0)" mutabakati icin -- Encounter
    // olarak daha once basariyla gonderilmis protokol Id'lerinin SU ANKI State'ini okur.
    // Sadece verilen id kumesiyle sinirli (SyncLogStore'daki "sent" kayitlar), tum tabloyu
    // taramaz.
    public async Task<Dictionary<int, byte>> GetStatesByIdsAsync(IReadOnlyCollection<int> protokolIds, CancellationToken ct = default)
    {
        var result = new Dictionary<int, byte>();
        if (protokolIds.Count == 0) return result;

        const string sql = "SELECT Id, State FROM hasta.protokol WHERE Id IN ({0})";
        var idList = protokolIds.ToList();
        var placeholders = idList.Select((_, i) => $"@id{i}").ToList();

        await using var conn = new SqlConnection(await ConnectionStringAsync(ct));
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(string.Format(sql, string.Join(",", placeholders)), conn);
        for (var i = 0; i < idList.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", idList[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetInt32(0)] = Convert.ToByte(reader.GetValue(1));
        return result;
    }

    private static ProtokolListItem MapProtokol(SqlDataReader reader) => new()
    {
        ProtokolId = reader.GetInt32(reader.GetOrdinal("ProtokolId")),
        HastaId = reader.GetInt32(reader.GetOrdinal("HastaId")),
        HastaAdi = GetString(reader, "HastaAdi"),
        HastaSoyadi = GetString(reader, "HastaSoyadi"),
        Fin = GetString(reader, "Fin"),
        DoktorId = GetInt(reader, "DoktorId"),
        DoktorAdi = GetString(reader, "DoktorAdi"),
        DoktorSoyadi = GetString(reader, "DoktorSoyadi"),
        BolumId = GetInt(reader, "BolumId"),
        BolumAdi = GetString(reader, "BolumAdi"),
        GelisTipiId = GetString(reader, "GelisTipiId"),
        ProtokolTipiId = GetByte(reader, "ProtokolTipiId"),
        AcilisTarihi = GetDateTime(reader, "AcilisTarihi"),
        KapanisTarihi = GetDateTime(reader, "KapanisTarihi"),
        State = Convert.ToByte(reader.GetValue(reader.GetOrdinal("State"))),
    };

    private static HastaRecord Map(SqlDataReader reader) => new()
    {
        Id = reader.GetInt32(reader.GetOrdinal("Id")),
        Adi = GetString(reader, "Adi"),
        Adi2 = GetString(reader, "Adi2"),
        Soyadi = GetString(reader, "Soyadi"),
        BabaAdi = GetString(reader, "BabaAdi"),
        DogumTarihi = GetDateTime(reader, "DogumTarihi"),
        CinsiyetId = GetString(reader, "CinsiyetId"),
        AktifHastaId = GetBool(reader, "AktifHastaId"),
        KanGrubuId = GetInt(reader, "KanGrubuId"),
        MedeniHaliId = GetInt(reader, "MedeniHaliId"),
        GSM = GetString(reader, "GSM"),
        SabitTel = GetString(reader, "SabitTel"),
        Email = GetString(reader, "Email"),
        TCKimlikNo = GetString(reader, "TCKimlikNo"),
        CreatedDate = GetDateTime(reader, "CreatedDate"),
        IsBizdeDogan = GetBool(reader, "IsBizdeDogan") ?? false,
        AnneTCKimlikNo = GetString(reader, "AnneTCKimlikNo"),
    };

    private static string? GetString(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTime? GetDateTime(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }

    private static bool? GetBool(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return Convert.ToBoolean(v);
    }

    private static int? GetInt(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return Convert.ToInt32(v);
    }

    private static byte? GetByte(SqlDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        if (r.IsDBNull(i)) return null;
        var v = r.GetValue(i);
        return Convert.ToByte(v);
    }
}
