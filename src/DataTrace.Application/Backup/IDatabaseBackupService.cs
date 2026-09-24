namespace DataTrace.Application.Backup;

public sealed class BackupFileInfo
{
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public required bool IntegrityOk { get; init; }
    public string? IntegrityDetail { get; init; }
}

public sealed class BackupRunResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? BackupPath { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public long TotalBytes { get; init; }
    public IReadOnlyList<BackupFileInfo> Files { get; init; } = [];
    public int DeletedBackupSets { get; init; }
}

public sealed class BackupStatusSnapshot
{
    public bool Enabled { get; init; }
    public string DailyTime { get; init; } = "02:30";
    public string BackupDirectory { get; init; } = "";
    public int RetentionDays { get; init; }
    public int MaxBackups { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public bool? LastSucceeded { get; init; }
    public string? LastError { get; init; }
    public string? LastBackupPath { get; init; }
    public long LastBackupBytes { get; init; }
    public int LastFileCount { get; init; }
    public DateTimeOffset? NextScheduledAt { get; init; }

    public bool IsStaleOrFailed(TimeSpan staleAfter)
    {
        if (LastSucceeded == false)
        {
            return true;
        }

        if (LastSuccessAt is null)
        {
            return true;
        }

        return DateTimeOffset.Now - LastSuccessAt.Value > staleAfter;
    }
}

public interface IDatabaseBackupService
{
    BackupStatusSnapshot GetStatus();
    bool NeedsCatchUpBackup();
    Task<BackupRunResult> RunBackupAsync(CancellationToken cancellationToken = default);
}
