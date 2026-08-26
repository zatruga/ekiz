namespace PusulaEHealthSync.Updater.Config;

public class DeployOptions
{
    // Gelistirme makinesindeki paylasilan publish ciktisi (UNC yol, orn. \\DEV-MAKINE\PusulaSyncPublish\Web).
    public required string SourceWebPath { get; set; }
    public required string SourceWorkerPath { get; set; }

    // Sunucudaki calisan uygulamanin bulundugu klasorler.
    public required string WebPath { get; set; }
    public required string WorkerPath { get; set; }

    // Guncelleme oncesi otomatik yedeklerin duracagi klasor.
    public required string BackupPath { get; set; }

    // Web dashboard ile bu servis arasindaki dosya-tabanli iletisim klasoru
    // (tetikleyici dosyalar + durum dosyasi).
    public required string ControlPath { get; set; }

    public string WorkerServiceName { get; set; } = "PusulaSyncWorker";
    public int MaxBackups { get; set; } = 5;
    public int PollSeconds { get; set; } = 3;
}
