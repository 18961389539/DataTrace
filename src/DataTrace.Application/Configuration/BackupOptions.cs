namespace DataTrace.Application.Configuration;

/// <summary>
/// Automatic DB backup options under Backup; customer.json may override nested keys.
/// Record retention is SystemSettings.RetentionYears + RetentionHostedService only.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>Enable scheduled backup. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Daily local time HH:mm. Default 02:30.</summary>
    public string DailyTime { get; set; } = "02:30";

    /// <summary>Backup root. Empty => DataRoot/backups; relative paths resolve under DataRoot.</summary>
    public string BackupDirectory { get; set; } = "";

    /// <summary>Keep backup sets for N days (with MaxBackups, whichever hits first). Default 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Max backup sets to keep; 0 = no count cap. Default 60.</summary>
    public int MaxBackups { get; set; } = 60;

    public TimeOnly GetDailyTimeOrDefault()
    {
        if (TimeOnly.TryParse(DailyTime, out var t))
        {
            return t;
        }

        return new TimeOnly(2, 30);
    }

    public string ResolveBackupDirectory(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(BackupDirectory))
        {
            return Path.Combine(dataRoot, "backups");
        }

        var path = BackupDirectory.Trim();
        return Path.IsPathRooted(path) ? path : Path.Combine(dataRoot, path);
    }
}
