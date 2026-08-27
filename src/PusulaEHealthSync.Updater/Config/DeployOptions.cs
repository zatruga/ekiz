namespace PusulaEHealthSync.Updater.Config;

public class DeployOptions
{
    // GitHub deposu -- ozel (private) depo oldugu icin URL'nin icine token gomulu olmali,
    // orn. "https://ghp_xxx@github.com/kullanici/repo.git" (appsettings.Production.json'da,
    // repoya asla girmez -- bkz. NeverOverwrite ayni mantik).
    public required string RepoUrl { get; set; }
    public string Branch { get; set; } = "main";

    // Sunucuda deponun klonlanacagi/guncellenecegi klasor (Web/Worker kaynak kodu buradan
    // "dotnet publish" ile derlenir, calisan uygulama klasorlerine degil).
    public required string RepoPath { get; set; }

    // Derleme once BURAYA yapilir (servisler CALISIRKEN, hicbir kesinti olmadan) -- ancak
    // derleme basariyla bittikten SONRA servisler durdurulup asil klasorlere kopyalanir.
    // Boylece indirme/derleme suresi kesinti suresine hic yansimiyor.
    public required string StagingPath { get; set; }

    public required string WebPath { get; set; }
    public required string WorkerPath { get; set; }
    public required string BackupPath { get; set; }
    public required string ControlPath { get; set; }

    public string WorkerServiceName { get; set; } = "PusulaSyncWorker";
    public int MaxBackups { get; set; } = 5;
    public int PollSeconds { get; set; } = 3;

    // Tetikleyici kontrolu her PollSeconds'ta bir oluyor (hizli olmali, kullanici butona
    // basinca beklemesin), ama GitHub'a "yeni surum var mi" sorgusu cok daha seyrek
    // yeterli -- gereksiz yere surekli git fetch atmamak icin ayri bir aralik.
    public int CheckIntervalSeconds { get; set; } = 60;
}
