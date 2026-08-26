using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Updater.Config;
using PusulaEHealthSync.Updater.Models;

namespace PusulaEHealthSync.Updater;

// Web dashboard'daki "Versiyon Guncelle" butonu bu sinifi DOGRUDAN cagirmiyor -- ayri bir
// Windows Service olarak calisiyoruz cunku calisan IIS/w3wp.exe surecinin kendi DLL'lerini
// UZERINE yazmasi (kendi kendini guncelleme) dosya kilidi ve yari-yolda-kesilen-istek
// riski tasiyor. Web sadece bir "tetikleyici" dosyasi yazar (bkz. UpdateWatcherService),
// asil is buradaki bagimsiz surecte olur.
[SupportedOSPlatform("windows")]
public class UpdateOrchestrator(IOptions<DeployOptions> optionsAccessor, ILogger<UpdateOrchestrator> logger)
{
    private readonly DeployOptions options = optionsAccessor.Value;

    // appsettings.Production.json (gercek Pusula/e-Health kimlik bilgilerini icerir) bu
    // otomasyonun HICBIR ASAMASINDA -- ne guncellemede ne geri almada -- yazilmaz/uzerine
    // yazilmaz. Bu, projenin "gercek sifreler asla otomatik islenmez" kuraliyla ayni ruhta.
    private static readonly string[] NeverOverwrite = ["appsettings.Production.json"];

    private const string AppOfflineFileName = "app_offline.htm";
    private const string AppOfflineHtml =
        "<html><body style=\"font-family:sans-serif;text-align:center;padding-top:80px;\">" +
        "<h2>Sistem güncelleniyor</h2><p>Birkaç saniye içinde tekrar deneyin.</p></body></html>";

    public async Task<UpdateStatus> RunUpdateAsync(string? requestedBy, CancellationToken ct)
    {
        var status = new UpdateStatus { State = UpdateState.InProgress, Operation = "Guncelleme", StartedAtUtc = DateTime.UtcNow, RequestedBy = requestedBy };
        try
        {
            logger.LogInformation("Guncelleme basliyor (istek: {RequestedBy}).", requestedBy);

            StopWindowsService(options.WorkerServiceName);
            await PutAppOfflineAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            var backupFolder = Path.Combine(options.BackupPath, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            await RunRobocopyAsync(options.WebPath, Path.Combine(backupFolder, "Web"), [], ct);
            await RunRobocopyAsync(options.WorkerPath, Path.Combine(backupFolder, "Worker"), [], ct);
            PruneOldBackups();

            await RunRobocopyAsync(options.SourceWebPath, options.WebPath, NeverOverwrite, ct);
            await RunRobocopyAsync(options.SourceWorkerPath, options.WorkerPath, NeverOverwrite, ct);

            RemoveAppOffline();
            StartWindowsService(options.WorkerServiceName);

            status.State = UpdateState.Success;
            status.Message = "Guncelleme basariyla tamamlandi.";
            status.BackupFolder = backupFolder;
            logger.LogInformation("Guncelleme tamamlandi. Yedek: {Backup}", backupFolder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Guncelleme basarisiz.");
            RemoveAppOffline(); // yari yolda kalirsa site kilitli kalmasin
            status.State = UpdateState.Failed;
            status.Message = ex.Message;
        }
        finally
        {
            status.FinishedAtUtc = DateTime.UtcNow;
        }
        return status;
    }

    public async Task<UpdateStatus> RunRollbackAsync(string? requestedBy, CancellationToken ct)
    {
        var status = new UpdateStatus { State = UpdateState.InProgress, Operation = "GeriAlma", StartedAtUtc = DateTime.UtcNow, RequestedBy = requestedBy };
        try
        {
            var latestBackup = Directory.Exists(options.BackupPath)
                ? Directory.GetDirectories(options.BackupPath).OrderByDescending(d => d).FirstOrDefault()
                : null;
            if (latestBackup is null)
                throw new InvalidOperationException("Geri donulecek bir yedek bulunamadi.");

            logger.LogInformation("Geri alma basliyor (istek: {RequestedBy}). Yedek: {Backup}", requestedBy, latestBackup);

            StopWindowsService(options.WorkerServiceName);
            await PutAppOfflineAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            await RunRobocopyAsync(Path.Combine(latestBackup, "Web"), options.WebPath, NeverOverwrite, ct);
            await RunRobocopyAsync(Path.Combine(latestBackup, "Worker"), options.WorkerPath, NeverOverwrite, ct);

            RemoveAppOffline();
            StartWindowsService(options.WorkerServiceName);

            status.State = UpdateState.Success;
            status.Message = $"'{Path.GetFileName(latestBackup)}' yedeğine geri dönüldü.";
            status.BackupFolder = latestBackup;
            logger.LogInformation("Geri alma tamamlandi.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Geri alma basarisiz.");
            RemoveAppOffline();
            status.State = UpdateState.Failed;
            status.Message = ex.Message;
        }
        finally
        {
            status.FinishedAtUtc = DateTime.UtcNow;
        }
        return status;
    }

    private void PruneOldBackups()
    {
        if (!Directory.Exists(options.BackupPath)) return;
        var dirs = Directory.GetDirectories(options.BackupPath).OrderByDescending(d => d).ToList();
        foreach (var old in dirs.Skip(options.MaxBackups))
        {
            try { Directory.Delete(old, recursive: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Eski yedek silinemedi: {Dir}", old); }
        }
    }

    private void StopWindowsService(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Servis durdurulamadi (bulunamadi olabilir): {Name}", serviceName);
        }
    }

    private void StartWindowsService(string serviceName)
    {
        try
        {
            using var sc = new ServiceController(serviceName);
            sc.Refresh();
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Servis baslatilamadi (bulunamadi olabilir): {Name}", serviceName);
        }
    }

    private Task PutAppOfflineAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(options.WebPath);
        return File.WriteAllTextAsync(Path.Combine(options.WebPath, AppOfflineFileName), AppOfflineHtml, ct);
    }

    private void RemoveAppOffline()
    {
        var path = Path.Combine(options.WebPath, AppOfflineFileName);
        if (File.Exists(path)) File.Delete(path);
    }

    // robocopy /MIR: hedefte olup kaynakta olmayan dosyalari SILER -- bu yuzden
    // appsettings.Production.json her zaman /XF ile disarida tutuluyor (excludeFiles).
    // Cikis kodlari 0-7 basari (kopyalanan/ekstra/farkli dosya kombinasyonlari), 8+ hata --
    // klasik robocopy tuzagi, normal process exit-code mantigiyla karistirilmamali.
    private async Task RunRobocopyAsync(string source, string destination, IReadOnlyList<string> excludeFiles, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var args = new List<string> { source, destination, "/MIR", "/R:5", "/W:2", "/NFL", "/NDL", "/NP" };
        foreach (var f in excludeFiles)
        {
            args.Add("/XF");
            args.Add(f);
        }

        var psi = new ProcessStartInfo("robocopy.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("robocopy baslatilamadi.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        logger.LogInformation("robocopy {Source} -> {Dest} exit={Code}", source, destination, process.ExitCode);
        if (process.ExitCode >= 8)
            throw new InvalidOperationException($"robocopy basarisiz (exit={process.ExitCode}): {source} -> {destination}\n{stdout}");
    }
}
