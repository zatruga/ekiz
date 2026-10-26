using Microsoft.Extensions.Options;
using PusulaEHealthSync.Config;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
using PusulaEHealthSync.Sync;

namespace PusulaEHealthSync;

// WEB VE SERVIS ICIN TEK SERVIS LISTESI (2026-10-23).
//
// Once iki Program.cs kendi listesini tutuyordu ve ikisi AYRISTI -- iki kez:
//   - 2026-09-02: PathologyReportSyncService yalnizca Web'e eklendi; servis DI dogrulamasinda
//     duvara carpti ve HIC baslamadi. Web etkilenmedigi icin fark edilmedi.
//   - Servis SyncLogStore'u SettingsStore OLMADAN kuruyordu; kayitlara ortam (Test/Live)
//     damgasi o tarafta dogru basilamazdi.
// Gonderim servise tasindiginda bu ayrisma "bir taraf gonderiyor, oteki goremiyor"
// demek olurdu. Tek liste bunu yapisal olarak imkansiz kiliyor.
//
// Arka plan donguleri (AddPusulaArkaPlanIsleri) BILEREK ayri: yalnizca servis cagiriyor.
// Ikisi birden cagirsaydi ayni protokol iki surecten ayni anda gonderilirdi.
public static class ServisKayitlari
{
    public static IServiceCollection AddPusulaSenkron(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PusulaOptions>(config.GetSection("Pusula"));
        services.Configure<EHealthOptions>(config.GetSection("EHealth"));
        services.Configure<SyncLogOptions>(config.GetSection("SyncLog"));

        services.AddSingleton<PusulaRepository>();
        services.AddSingleton<EHealthTokenCache>();
        services.AddHttpClient<EHealthClient>();
        services.AddHttpClient<Mapping.LoincLookup>();

        // Tum depolar AYNI dosyada. Goreli yol uygulama klasorune gore cozuluyor: servis
        // System32'den baslatildigi icin calisma dizini guvenilir degil.
        static string Yol(IServiceProvider sp)
        {
            var yol = sp.GetRequiredService<IOptions<SyncLogOptions>>().Value.DbPath;
            return Path.IsPathRooted(yol) ? yol : Path.Combine(AppContext.BaseDirectory, yol);
        }

        services.AddSingleton(sp => new SettingsStore(Yol(sp)));
        // SettingsStore'u ALIYOR: her kaydin hangi ortama (Test/Live) gittigini damgalamak icin.
        services.AddSingleton(sp => new SyncLogStore(Yol(sp), sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton(sp => new BolumMappingStore(Yol(sp)));
        services.AddSingleton(sp => new LabTestLoincStore(Yol(sp)));
        services.AddSingleton(sp => new HizmetMappingStore(Yol(sp)));
        services.AddSingleton(sp => new UserAccountStore(Yol(sp)));
        services.AddSingleton(sp => new YatanArsivStore(Yol(sp)));

        services.AddSingleton<PatientSyncService>();
        services.AddSingleton<PractitionerSyncService>();
        services.AddSingleton<ConditionSyncService>();
        services.AddSingleton<VitalSignsSyncService>();
        services.AddSingleton<ProcedureSyncService>();
        services.AddSingleton<EncounterSyncService>();
        services.AddSingleton<CompositionSyncService>();
        services.AddSingleton<LabResultSyncService>();
        services.AddSingleton<RadiologyReportSyncService>();
        services.AddSingleton<PathologyReportSyncService>();
        services.AddSingleton<DeleteService>();
        services.AddSingleton<TekilGonderimService>();
        services.AddSingleton<ProtocolFullSyncService>();
        services.AddSingleton<PendingWorkService>();
        services.AddSingleton<CancellationSyncService>();
        services.AddSingleton<ProtokolBagiDoldurmaService>();

        services.AddSingleton<GunSonuRaporService>();
        services.AddSingleton<MailSender>();
        services.AddSingleton<MailAyarlari>();
        services.AddSingleton<ProtokolDurumService>();
        return services;
    }

    // YALNIZCA SERVIS (PusulaSyncWorker) CAGIRIR. Bkz. sinifin basindaki not.
    public static IServiceCollection AddPusulaArkaPlanIsleri(this IServiceCollection services)
    {
        // Otomatik gonderim + iptal senkronu + takilanlar + gun sonu raporu. VARSAYILAN
        // KAPALI: Ayarlar'daki "Otomatik gonderim" acilmadan tek kayit gondermez.
        services.AddHostedService<AutoSyncWorker>();
        // Protokol bagi doldurma -- gonderim turundan bagimsiz (bkz. ProtokolBagiDoldurmaWorker).
        services.AddHostedService<ProtokolBagiDoldurmaWorker>();
        return services;
    }
}
