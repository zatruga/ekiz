namespace PusulaEHealthSync.Updater.Models;

public enum UpdateState { Idle, InProgress, Success, Failed }

public class UpdateStatus
{
    public UpdateState State { get; set; } = UpdateState.Idle;

    // "Guncelleme" | "GeriAlma"
    public string? Operation { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? BackupFolder { get; set; }
    public string? RequestedBy { get; set; }
    public string? CommitHash { get; set; }
    public string? CommitMessage { get; set; }
}
