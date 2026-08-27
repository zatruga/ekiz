using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PusulaEHealthSync.Updater.Config;
using PusulaEHealthSync.Updater.Models;

namespace PusulaEHealthSync.Updater;

// Web dashboard ile bu servis arasinda ortak bir process/port yok -- dosya-tabanli basit
// bir "kuyruk" kullaniyoruz: Web bir tetikleyici dosya yazar, biz PollSeconds araligiyla
// kontrol klasorunu tariyoruz. Ayni anda tek guncelleme/geri-alma islenir (dongu tek
// thread'de calisiyor), bu yuzden yaris durumu riski yok.
public class UpdateWatcherService(
    IOptions<DeployOptions> optionsAccessor,
    UpdateOrchestrator orchestrator,
    ILogger<UpdateWatcherService> logger) : BackgroundService
{
    private readonly DeployOptions options = optionsAccessor.Value;

    private const string UpdateTriggerFile = "update.trigger";
    private const string RollbackTriggerFile = "rollback.trigger";
    private const string StatusFile = "update-status.json";
    private const string CheckFile = "update-check.json";
    private const string HistoryFile = "update-history.json";
    private const int MaxHistoryEntries = 20;

    private DateTime lastCheckedAtUtc = DateTime.MinValue;

    // Web tarafi (UpdateStatusView) State'i string olarak okuyor -- enum'un varsayilan
    // sayisal serilesmesi (0,1,2,3) sessizce deserialize hatasina yol acip Web'de hicbir
    // sey degismiyormus gibi gorunmesine sebep oluyordu (bkz. canli olayda bulundu, ilk
    // GitHub testinde). String olarak yazmak hem bunu cozuyor hem dosyayi elle okurken
    // (Get-Content) anlasilir kiliyor.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(options.ControlPath);
        WriteStatus(new UpdateStatus { State = UpdateState.Idle, Message = "Servis baslatildi, tetikleyici bekleniyor." });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updateTrigger = Path.Combine(options.ControlPath, UpdateTriggerFile);
                var rollbackTrigger = Path.Combine(options.ControlPath, RollbackTriggerFile);

                if (File.Exists(updateTrigger))
                {
                    var requestedBy = SafeReadAndDelete(updateTrigger);
                    // Once "Isleniyor" yazilmazsa Web tarafi tum sure boyunca ("Bekliyor")
                    // hicbir sey degismiyormus gibi goruyor -- canli olayda bulundu, kullanici
                    // emin olamayip butona birden fazla kez basmisti.
                    WriteStatus(new UpdateStatus { State = UpdateState.InProgress, Operation = "Guncelleme", StartedAtUtc = DateTime.UtcNow, RequestedBy = requestedBy });
                    var status = await orchestrator.RunUpdateAsync(requestedBy, stoppingToken);
                    WriteStatus(status);
                    AppendHistory(status);
                }
                else if (File.Exists(rollbackTrigger))
                {
                    var requestedBy = SafeReadAndDelete(rollbackTrigger);
                    WriteStatus(new UpdateStatus { State = UpdateState.InProgress, Operation = "GeriAlma", StartedAtUtc = DateTime.UtcNow, RequestedBy = requestedBy });
                    var status = await orchestrator.RunRollbackAsync(requestedBy, stoppingToken);
                    WriteStatus(status);
                    AppendHistory(status);
                }
                else if (DateTime.UtcNow - lastCheckedAtUtc >= TimeSpan.FromSeconds(options.CheckIntervalSeconds))
                {
                    // Sadece tetikleyici yokken (guncelleme surerken degil) kontrol et --
                    // ayni anda hem gercek publish hem de bu hafif fetch calismasin diye.
                    lastCheckedAtUtc = DateTime.UtcNow;
                    var deployedHash = ReadStatus()?.CommitHash;
                    var check = await orchestrator.CheckRemoteAsync(deployedHash, stoppingToken);
                    WriteCheck(check);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Kontrol dongusunde beklenmeyen hata.");
            }

            await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken);
        }
    }

    private string? SafeReadAndDelete(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            File.Delete(path);
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tetikleyici dosya okunamadi/silinemedi: {Path}", path);
            return null;
        }
    }

    private void WriteStatus(UpdateStatus status)
    {
        var path = Path.Combine(options.ControlPath, StatusFile);
        var json = JsonSerializer.Serialize(status, JsonOptions);
        File.WriteAllText(path, json);
    }

    private UpdateStatus? ReadStatus()
    {
        var path = Path.Combine(options.ControlPath, StatusFile);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(path), JsonOptions); }
        catch { return null; }
    }

    private void WriteCheck(RemoteCheckStatus check)
    {
        var path = Path.Combine(options.ControlPath, CheckFile);
        var json = JsonSerializer.Serialize(check, JsonOptions);
        File.WriteAllText(path, json);
    }

    // Her tamamlanan guncelleme/geri-alma islemini kalici bir listeye ekler -- kullanici
    // "her sürüm bilgisi alt alta görünsün, ne zaman ne değiştirdiğimi bileyim" istedi
    // (2026-08-27). En yeni basta, en fazla MaxHistoryEntries kayit tutuluyor.
    private void AppendHistory(UpdateStatus status)
    {
        var path = Path.Combine(options.ControlPath, HistoryFile);
        List<UpdateStatus> history;
        try
        {
            history = File.Exists(path)
                ? JsonSerializer.Deserialize<List<UpdateStatus>>(File.ReadAllText(path), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Gecmis dosyasi okunamadi, sifirdan baslatiliyor.");
            history = [];
        }

        history.Insert(0, status);
        if (history.Count > MaxHistoryEntries)
            history.RemoveRange(MaxHistoryEntries, history.Count - MaxHistoryEntries);

        File.WriteAllText(path, JsonSerializer.Serialize(history, JsonOptions));
    }
}
