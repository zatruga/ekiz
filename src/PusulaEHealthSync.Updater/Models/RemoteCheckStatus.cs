namespace PusulaEHealthSync.Updater.Models;

public class RemoteCheckStatus
{
    public string? DeployedCommitHash { get; set; }

    // UpdateStatus.Version ile ayni gerekce (2026-08-31) -- su an calisan surum ve
    // guncellenirse ulasilacak surum, kullaniciya once numara olarak gosteriliyor.
    public string? DeployedVersion { get; set; }
    public string? PendingVersion { get; set; }

    // GitHub'da olup henuz sunucuya alinmamis TUM commit'ler, en yeni basta -- sadece son
    // commit degil, guncelleme yapilirsa neler geleceginin tam listesi (2026-08-27, kullanici
    // "guncellemeden once neler gelecek onceden bileyim" istedi).
    public List<PendingCommit> PendingCommits { get; set; } = [];

    public bool HasUpdate { get; set; }
    public DateTime? CheckedAtUtc { get; set; }
    public string? Error { get; set; }
}

public class PendingCommit
{
    public string Hash { get; set; } = "";
    public string Message { get; set; } = "";
}
