namespace PusulaEHealthSync;

// Sistemdeki IKI zaman tabani arasindaki TEK gecis noktasi.
//
//   Pusula (SQL Server)  -> YEREL saat (Baki, +04:00). hasta.protokol.AcilisTarihi,
//                           EPulse.IstemZamani, GenelMuayene.ModifiedDate ... hepsi yerel.
//   SyncLog (SQLite)     -> UTC ("2026-09-09T07:49:51.1549240Z", ISO 8601, Z sonekli).
//
// NEDEN BU SINIF VAR (2026-09-09, kullanici ile birlikte karar): "+04:00" bilgisi 8 ayri
// mapper'da ToAzInstant olarak kopyalanmisti ama SyncLog tarafina HIC uygulanmamisti. Bu iki
// gercek hataya yol aciyordu:
//
//   1. Genel Bakis'ta yerel gece yarisi degerleri dogrudan UTC alani ile karsilastiriliyordu
//      (PeriodFromUtc adi "Utc" diyor ama icerigi yereldi) -- gun siniri fiilen 04:00'e
//      kayiyordu.
//   2. Gunluk trend grafigi gunu UTC'ye gore gruplarken, Baki saatiyle 00:00-04:00 arasi
//      yapilan her gonderim BIR ONCEKI gune dusuyordu (gece nobeti islemleri dunde gorunuyor).
//
// DEPOLAMA BILEREK UTC KALDI: SyncLog'un ISO 8601 + "Z" bicimi metin olarak dogru siralaniyor
// ve sorgular buna dayaniyor; yerele cevirmek 806 satirlik bir migration ve -- daha kotusu --
// migration oncesi alinmis yedeklerin yerel mi UTC mi oldugunun ayirt edilememesi demekti.
// Cozum depolamayi degistirmek degil, sinirin NEREDE oldugunu tek yerde tanimlamak.
//
// DST YOK: Azerbaycan 2016'da yaz saati uygulamasini kaldirdi, ofset yil boyu sabit +04:00.
// Bu yuzden TimeZoneInfo yerine sabit bir TimeSpan yeterli (ve makineden bagimsiz -- sunucu
// baska bir saat diliminde calissa bile davranis degismez).
public static class AzTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(4);

    // Pusula'dan gelen yerel saat -> UTC. SyncLog.CreatedAtUtc ile karsilastirmadan ONCE
    // mutlaka bundan gecirilmeli (orn. "epikriz son gonderimden sonra degismis mi?").
    public static DateTime ToUtc(DateTime azLocal)
        => DateTime.SpecifyKind(azLocal - Offset, DateTimeKind.Utc);

    // UTC -> Baki yerel saati. Ekranda gosterilen her SyncLog zamani bundan gecmeli;
    // kullanici Pusula ekranlarindaki saatle ayni degeri gormeli.
    public static DateTime ToLocal(DateTime utc)
        => DateTime.SpecifyKind(utc + Offset, DateTimeKind.Unspecified);

    // FHIR'a giden yerel zaman damgasi. Onceden bu satir 8 mapper'da ayri ayri duruyordu
    // (EncounterMapper, CompositionMapper, ConditionMapper, ProcedureMapper,
    // LabResultObservationMapper, RadiologyReportMapper, PathologyReportMapper,
    // PathologyFindingMapper, PathologyCompositionMapper) -- hepsi buraya delege ediyor.
    public static string ToAzInstant(DateTime azLocal)
        => azLocal.ToString("yyyy-MM-ddTHH:mm:ss") + "+04:00";

    // SQLite'ta UTC saklanan bir sutunu YEREL gune gore gruplamak icin kaydirma ifadesi.
    // Kullanimi: substr(datetime(CreatedAtUtc, '+4 hours'), 1, 10)
    public const string SqliteShiftToLocal = "'+4 hours'";
}
