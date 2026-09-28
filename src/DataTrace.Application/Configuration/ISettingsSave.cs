namespace DataTrace.Application.Configuration;

/// <summary>设置已写入，或被地址/取值规则拦住。</summary>
public sealed class SettingsSaveResult
{
    public SystemSettingsSnapshot? Saved { get; init; }

    public string? ValidationMessage { get; init; }

    public string? AuditError { get; init; }

    public string? Failure { get; init; }
}

/// <summary>
/// 保存后的设置快照。页面用它刷新编辑副本，不把可改的实体再交回去。
/// </summary>
public sealed class SystemSettingsSnapshot
{
    public required int ScanIntervalMs { get; init; }
    public required int WriteRetryCount { get; init; }
    public required int WriteRetryDelayMs { get; init; }
    public required int RetentionYears { get; init; }
    public required int AuditRetentionYears { get; init; }
    public required int MesTimeoutSeconds { get; init; }
    public required int ShiftStartHour { get; init; }
    public required int ShiftLengthHours { get; init; }
    public required bool CollectEnabled { get; init; }
    public required bool MesEnabled { get; init; }
    public required bool SimulatorAutoRun { get; init; }
    public required int SimulatorIntervalMs { get; init; }
    public required int SimulatorNgPercent { get; init; }
    public required int SimulatorPalletPool { get; init; }
    public string? MesEndpoint { get; init; }
    public string? AlarmWebhookUrl { get; init; }

    public static SystemSettingsSnapshot From(Domain.Entities.SystemSettings settings) => new()
    {
        ScanIntervalMs = settings.ScanIntervalMs,
        WriteRetryCount = settings.WriteRetryCount,
        WriteRetryDelayMs = settings.WriteRetryDelayMs,
        RetentionYears = settings.RetentionYears,
        AuditRetentionYears = settings.AuditRetentionYears,
        MesTimeoutSeconds = settings.MesTimeoutSeconds,
        ShiftStartHour = settings.ShiftStartHour,
        ShiftLengthHours = settings.ShiftLengthHours,
        CollectEnabled = settings.CollectEnabled,
        MesEnabled = settings.MesEnabled,
        SimulatorAutoRun = settings.SimulatorAutoRun,
        SimulatorIntervalMs = settings.SimulatorIntervalMs,
        SimulatorNgPercent = settings.SimulatorNgPercent,
        SimulatorPalletPool = settings.SimulatorPalletPool,
        MesEndpoint = settings.MesEndpoint,
        AlarmWebhookUrl = settings.AlarmWebhookUrl
    };
}

/// <summary>保存系统设置：补丁打在库里的最新一行上，审计失败不回滚配置。</summary>
public interface ISettingsSave
{
    Task<SettingsSaveResult> SaveAsync(
        SettingsEdit edit,
        SettingsEdit loaded,
        string userName,
        CancellationToken cancellationToken = default);
}
