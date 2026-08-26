namespace PusulaEHealthSync.Web.Config;

// Updater servisiyle ortak dosya-tabanli kontrol klasoru -- Web burada sadece tetikleyici
// dosya yazar ve durum dosyasini okur, gercek kopyalama/servis islemleri Updater'da
// (bkz. PusulaEHealthSync.Updater/UpdateOrchestrator.cs) ayri bir surecte yapilir.
public class DeployOptions
{
    public required string ControlPath { get; set; }
}
