using PusulaEHealthSync.Persistence;
using PusulaEHealthSync.Reporting;
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

// SERVIS BEKCISI (2026-10-23) -- bkz. ServisBekciService.
//
// VERITABANI YOLU GONDERIM SERVISININ KENDI AYARINDAN: bekcinin okumasi gereken nabiz ve
// e-posta ayarlari tam olarak o servisin yazdigi dosyada. Yolu ayrica guncelleyicinin
// ayarina yazmak, sunucuda elle duzenlenecek bir dosya daha ve ayrisabilecek ikinci bir
// kopya demek olurdu. appsettings.Production.json once, appsettings.json yedek; ikisinde
// de yoksa guncelleyicinin kendi "SyncLog:DbPath" ayari.
//
// YOL BULUNAMAZSA BEKCI EKLENMEZ, HATA FIRLATILMAZ: bekci yardimci bir is. Firlatsaydi
// guncelleyicinin TAMAMI baslamaz, guncelleme kanali da onunla olurdu -- sorunu
// duzeltecek surumu dagitmanin yolu kalmazdi.
var bekciDbYolu = BekciDbYolu(builder.Configuration);
if (bekciDbYolu is not null)
{
    builder.Services.AddSingleton(_ => new SettingsStore(bekciDbYolu));
    builder.Services.AddSingleton<MailSender>();
    builder.Services.AddSingleton<MailAyarlari>();
    builder.Services.AddHostedService<ServisBekciService>();
}

// BEKCI DE GUNCELLEYICIYI DUSURMEMELI: bir arka plan servisi yakalanmamis hata atarsa
// .NET varsayilan olarak TUM sureci durdurur. Bekci dongusu hatalari zaten yakaliyor;
// bu ikinci emniyet.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

var host = builder.Build();
if (bekciDbYolu is null)
    host.Services.GetRequiredService<ILogger<ServisBekciService>>().LogError(
        "Servis bekcisi BASLATILMADI: veritabani yolu bulunamadi (Deploy:WorkerPath altindaki appsettings'te "
        + "ve guncelleyicinin kendi ayarinda SyncLog:DbPath yok). Guncelleme kanali etkilenmez.");
host.Run();

static string? BekciDbYolu(IConfiguration config)
{
    var workerKlasoru = config["Deploy:WorkerPath"];
    if (!string.IsNullOrWhiteSpace(workerKlasoru))
    {
        var workerAyari = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(workerKlasoru, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(workerKlasoru, "appsettings.Production.json"), optional: true)
            .Build();
        if (workerAyari["SyncLog:DbPath"] is { Length: > 0 } yol) return yol;
    }
    return config["SyncLog:DbPath"] is { Length: > 0 } kendi ? kendi : null;
}
