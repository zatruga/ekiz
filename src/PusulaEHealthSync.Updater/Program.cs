using PusulaEHealthSync.Updater;
using PusulaEHealthSync.Updater.Config;

var builder = Host.CreateApplicationBuilder(args);

// IIS/Web ve Worker'dan bagimsiz, ayri bir Windows Service olarak kurulur (bkz.
// UpdateOrchestrator ustundeki not -- kendi kendini guncelleyen bir surecin
// dosya kilidi/yari-yolda-kesilen-istek riskini onlemek icin).
builder.Services.AddWindowsService(options => options.ServiceName = "PusulaSync Updater");

builder.Services.Configure<DeployOptions>(builder.Configuration.GetSection("Deploy"));
builder.Services.AddSingleton<UpdateOrchestrator>();
builder.Services.AddHostedService<UpdateWatcherService>();

var host = builder.Build();
host.Run();
