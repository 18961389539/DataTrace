namespace DataTrace.Application.Configuration;

/// <summary>
/// 自动数据库备份与可选记录清理。绑定配置节 Backup；customer.json 同名嵌套可覆盖。
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>是否启用定时备份。默认 true。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>每日备份本地时间，格式 HH:mm。默认 02:30。</summary>
    public string DailyTime { get; set; } = "02:30";

    /// <summary>备份根目录。空则 DataRoot/backups；相对路径相对于 DataRoot。</summary>
    public string BackupDirectory { get; set; } = "";

    /// <summary>备份集保留天数；与 MaxBackups 谁先到谁清理。默认 30。</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>最多保留备份集数量；0 表示不限制数量。默认 60。</summary>
    public int MaxBackups { get; set; } = 60;

    public RecordRetentionOptions RecordRetention { get; set; } = new();

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

public sealed class RecordRetentionOptions
{
    /// <summary>是否在备份成功后清理过期记录。默认 false。</summary>
    public bool Enabled { get; set; }

    /// <summary>保留最近多少天的采集记录。默认 1095（约 3 年）。</summary>
    public int KeepDays { get; set; } = 1095;
}
