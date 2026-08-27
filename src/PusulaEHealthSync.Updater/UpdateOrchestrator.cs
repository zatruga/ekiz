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
//
// AKIS (2026-08-26, GitHub'a gecis): git pull + dotnet publish, servisler CALISIRKEN,
// once bir "Staging" klasorune yapilir -- bu adim yavas olabilir (indirme+derleme) ama
// kesinti suresine hic yansimiyor. Servisler SADECE staging'den asil klasorlere hizli
// dosya kopyalama suresince durur.
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
        "<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:sans-serif;text-align:center;padding-top:80px;\">" +
        "<h2>Sistem güncelleniyor</h2><p>Birkaç saniye içinde tekrar deneyin.</p></body></html>";

    public async Task<UpdateStatus> RunUpdateAsync(string? requestedBy, CancellationToken ct)
    {
        var status = new UpdateStatus { State = UpdateState.InProgress, Operation = "Guncelleme", StartedAtUtc = DateTime.UtcNow, RequestedBy = requestedBy };
        try
        {
            logger.LogInformation("Guncelleme basliyor (istek: {RequestedBy}).", requestedBy);

            var (hash, message) = await SyncRepositoryAsync(ct);
            status.CommitHash = hash;
            status.CommitMessage = message;

            var stagingWeb = Path.Combine(options.StagingPath, "Web");
            var stagingWorker = Path.Combine(options.StagingPath, "Worker");
            await RunProcessAsync("dotnet", ["publish", "src/PusulaEHealthSync.Web", "-c", "Release", "-o", stagingWeb], options.RepoPath, ct);
            await RunProcessAsync("dotnet", ["publish", "src/PusulaEHealthSync", "-c", "Release", "-o", stagingWorker], options.RepoPath, ct);

            StopWindowsService(options.WorkerServiceName);
            await PutAppOfflineAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            var backupFolder = Path.Combine(options.BackupPath, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            await RunRobocopyAsync(options.WebPath, Path.Combine(backupFolder, "Web"), [], ct);
            await RunRobocopyAsync(options.WorkerPath, Path.Combine(backupFolder, "Worker"), [], ct);
            PruneOldBackups();

            await RunRobocopyAsync(stagingWeb, options.WebPath, NeverOverwrite, ct);
            await RunRobocopyAsync(stagingWorker, options.WorkerPath, NeverOverwrite, ct);

            RemoveAppOffline();
            StartWindowsService(options.WorkerServiceName);

            status.State = UpdateState.Success;
            status.Message = "Guncelleme basariyla tamamlandi.";
            status.BackupFolder = backupFolder;
            logger.LogInformation("Guncelleme tamamlandi. Commit: {Hash} Yedek: {Backup}", hash, backupFolder);
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

    // Sadece "yeni surum var mi" diye bakar -- calisan dosyalara ASLA dokunmaz. RepoPath
    // henuz klonlanmamissa (ilk guncelleme hic calistirilmamissa) kontrol atlanir, ilk
    // "Versiyon Guncelle" tiklamasi zaten klonu olusturacak.
    public async Task<RemoteCheckStatus> CheckRemoteAsync(string? deployedCommitHash, CancellationToken ct)
    {
        var status = new RemoteCheckStatus { DeployedCommitHash = deployedCommitHash, CheckedAtUtc = DateTime.UtcNow };
        try
        {
            if (!Directory.Exists(Path.Combine(options.RepoPath, ".git")))
            {
                status.Error = "Henuz hic guncelleme calistirilmadi, kontrol icin once bir kere 'Versiyon Guncelle' calistirilmali.";
                return status;
            }

            await RunProcessAsync("git", ["fetch", "origin", options.Branch], options.RepoPath, ct);
            var hash = (await RunProcessAsync("git", ["log", $"origin/{options.Branch}", "-1", "--format=%H"], options.RepoPath, ct)).Trim();
            var message = (await RunProcessAsync("git", ["log", $"origin/{options.Branch}", "-1", "--format=%s"], options.RepoPath, ct)).Trim();

            status.LatestCommitHash = hash;
            status.LatestCommitMessage = message;
            status.HasUpdate = !string.IsNullOrEmpty(hash) && !string.Equals(hash, deployedCommitHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Uzak surum kontrolu basarisiz.");
            status.Error = ex.Message;
        }
        return status;
    }

    // RepoPath yoksa (ilk calistirma) klonlar, varsa mevcut klonu origin/branch'e sifirlar.
    // Bu klon SADECE bu servis tarafindan dokunulur (elle duzenlenmez), bu yuzden "git pull"
    // yerine fetch+hard-reset kullanmak daha ongorulebilir -- yerel bir sapma birikemez.
    private async Task<(string Hash, string Message)> SyncRepositoryAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(options.RepoPath, ".git")))
        {
            Directory.CreateDirectory(options.RepoPath);
            await RunProcessAsync("git", ["clone", "--branch", options.Branch, options.RepoUrl, "."], options.RepoPath, ct);
        }
        else
        {
            await RunProcessAsync("git", ["fetch", "origin", options.Branch], options.RepoPath, ct);
            await RunProcessAsync("git", ["reset", "--hard", $"origin/{options.Branch}"], options.RepoPath, ct);
        }

        var hash = (await RunProcessAsync("git", ["log", "-1", "--format=%H"], options.RepoPath, ct)).Trim();
        var message = (await RunProcessAsync("git", ["log", "-1", "--format=%s"], options.RepoPath, ct)).Trim();
        return (hash, message);
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
        return File.WriteAllTextAsync(Path.Combine(options.WebPath, AppOfflineFileName), AppOfflineHtml, System.Text.Encoding.UTF8, ct);
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

    // git/dotnet gibi normal process'ler icin ortak calistirici -- robocopy'nin aksine
    // buradaki her sey icin 0 disinda bir exit code = hata.
    private async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> args, string workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // git commit mesajlarini (Turkce karakterler dahil) UTF-8 olarak yaziyor, ama
            // Process.Start varsayilan olarak konsolun sistem kod sayfasiyla okuyor --
            // bu ikisi uyusmayinca "SÃ¼rÃ¼m" gibi bozuk metin ortaya cikiyordu (canli
            // olayda bulundu, Guncelle sayfasindaki commit mesaji goruntusunde).
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} baslatilamadi.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        logger.LogInformation("{FileName} {Args} (in {Dir}) exit={Code}", fileName, string.Join(' ', args), workingDirectory, process.ExitCode);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} {string.Join(' ', args)} basarisiz (exit={process.ExitCode}):\n{stdout}\n{stderr}");

        return stdout;
    }
}
