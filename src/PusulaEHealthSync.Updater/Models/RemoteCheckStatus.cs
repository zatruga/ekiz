namespace PusulaEHealthSync.Updater.Models;

public class RemoteCheckStatus
{
    public string? LatestCommitHash { get; set; }
    public string? LatestCommitMessage { get; set; }
    public string? DeployedCommitHash { get; set; }
    public bool HasUpdate { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    public string? Error { get; set; }
}
