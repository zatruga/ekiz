using PusulaEHealthSync;
using PusulaEHealthSync.Config;
using PusulaEHealthSync.Db;
using PusulaEHealthSync.EHealth;
using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Sync;

var builder = Host.CreateApplicationBuilder(args);

// KULLANICI ISTEGI (2026-08-22): hastane sunucusuna kurulum -- Windows Service olarak
// baslatilinca (SCM tarafindan) dogru sekilde calisması icin gerekli. Konsoldan
// "dotnet run" ile calistirilinca (mevcut yerel gelistirme deneyimi) bu cagri NO-OP --
// sadece process gercekten bir Windows Service olarak baslatildiginda devreye girer.
builder.Services.AddWindowsService(options => options.ServiceName = "PusulaEHealthSync Worker");

builder.Services.Configure<PusulaOptions>(builder.Configuration.GetSection("Pusula"));
builder.Services.Configure<EHealthOptions>(builder.Configuration.GetSection("EHealth"));
builder.Services.Configure<SyncLogOptions>(builder.Configuration.GetSection("SyncLog"));

builder.Services.AddSingleton<PusulaRepository>();
builder.Services.AddHttpClient<EHealthClient>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    var path = Path.IsPathRooted(options.DbPath)
        ? options.DbPath
        : Path.Combine(AppContext.BaseDirectory, options.DbPath);
    return new SyncLogStore(path);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    var path = Path.IsPathRooted(options.DbPath)
        ? options.DbPath
        : Path.Combine(AppContext.BaseDirectory, options.DbPath);
    return new SettingsStore(path);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SyncLogOptions>>().Value;
    var path = Path.IsPathRooted(options.DbPath)
        ? options.DbPath
        : Path.Combine(AppContext.BaseDirectory, options.DbPath);
    return new BolumMappingStore(path);
});
builder.Services.AddSingleton<PatientSyncService>();
builder.Services.AddSingleton<PractitionerSyncService>();
builder.Services.AddSingleton<ConditionSyncService>();
builder.Services.AddSingleton<ProcedureSyncService>();
builder.Services.AddSingleton<EncounterSyncService>();
builder.Services.AddSingleton<CompositionSyncService>();
builder.Services.AddSingleton<LabResultSyncService>();
builder.Services.AddSingleton<RadiologyReportSyncService>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
