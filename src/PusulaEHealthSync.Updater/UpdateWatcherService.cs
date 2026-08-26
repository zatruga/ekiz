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
                    var status = await orchestrator.RunUpdateAsync(requestedBy, stoppingToken);
                    WriteStatus(status);
                }
                else if (File.Exists(rollbackTrigger))
                {
                    var requestedBy = SafeReadAndDelete(rollbackTrigger);
                    var status = await orchestrator.RunRollbackAsync(requestedBy, stoppingToken);
                    WriteStatus(status);
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
}
