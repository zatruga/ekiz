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

    // KULLANICI ISTEGI (2026-08-31): "sürüm kodlarını 4480d69 gibi değilde daha anlaşılır
    // bir durum olsun ... 1.5.23 gibi" -- MAJOR.MINOR.PATCH semantiği: MAJOR=platform/dil
    // nesli (nadiren degisir), MINOR=buyuk ozellik/modul (orn. Laboratuvar, sonra
    // Radyoloji), PATCH=her kucuk/beta degisiklik. Repo kokundeki VERSION dosyasindan
    // okunuyor (bkz. UpdateOrchestrator.SyncRepositoryAsync) -- git commit hash'i teknik
    // referans olarak hala saklaniyor ama kullaniciya birincil olarak Version gosteriliyor.
    public string? Version { get; set; }
}
