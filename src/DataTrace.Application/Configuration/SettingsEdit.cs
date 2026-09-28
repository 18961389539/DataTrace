using DataTrace.Domain.Entities;

namespace DataTrace.Application.Configuration;

/// <summary>
/// 系统设置页上能改的字段。数值用可空承接：输入框被清空时是 null，
/// 不能让控件把「保留年数」悄悄夹成下限后再保存。
/// </summary>
public sealed class SettingsEdit
{
    public int? ScanIntervalMs { get; set; }
    public int? WriteRetryCount { get; set; }
    public int? WriteRetryDelayMs { get; set; }
    public int? RetentionYears { get; set; }
    public int? AuditRetentionYears { get; set; }
    public int? MesTimeoutSeconds { get; set; }
    public int? ShiftStartHour { get; set; }
    public int? ShiftLengthHours { get; set; }
    public bool CollectEnabled { get; set; }
    public bool MesEnabled { get; set; }
    public string? MesEndpoint { get; set; }
    public string? AlarmWebhookUrl { get; set; }

    public SettingsEdit Clone() => (SettingsEdit)MemberwiseClone();

    public bool DiffersFrom(SettingsEdit other)
        => ScanIntervalMs != other.ScanIntervalMs
           || WriteRetryCount != other.WriteRetryCount
           || WriteRetryDelayMs != other.WriteRetryDelayMs
           || RetentionYears != other.RetentionYears
           || AuditRetentionYears != other.AuditRetentionYears
           || MesTimeoutSeconds != other.MesTimeoutSeconds
           || ShiftStartHour != other.ShiftStartHour
           || ShiftLengthHours != other.ShiftLengthHours
           || CollectEnabled != other.CollectEnabled
           || MesEnabled != other.MesEnabled
           || !string.Equals(MesEndpoint ?? "", other.MesEndpoint ?? "", StringComparison.Ordinal)
           || !string.Equals(AlarmWebhookUrl ?? "", other.AlarmWebhookUrl ?? "", StringComparison.Ordinal);

    public static SettingsEdit From(SystemSettings settings) => new()
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
        MesEndpoint = settings.MesEndpoint,
        AlarmWebhookUrl = settings.AlarmWebhookUrl
    };
}

/// <summary>
/// 保存设置：只把这一页相对载入时改过的字段打到库里的最新一行上。
/// 整行覆盖会把另一会话刚保存的字段回滚。
/// </summary>
public static class SettingsEditRules
{
    public static string? UrlError(SettingsEdit edit)
    {
        if (edit.MesEnabled && !IsHttpUrl(edit.MesEndpoint))
        {
            return "已启用 MES 推送，但地址不是合法的 http(s) 地址";
        }

        if (!string.IsNullOrWhiteSpace(edit.AlarmWebhookUrl) && !IsHttpUrl(edit.AlarmWebhookUrl))
        {
            return "异常呼叫地址不是合法的 http(s) 地址";
        }

        return null;
    }

    public static string? RequiredError(SettingsEdit edit)
        => edit.ScanIntervalMs is null ? "扫描间隔不能为空"
            : edit.WriteRetryCount is null ? "写回重试次数不能为空"
            : edit.WriteRetryDelayMs is null ? "写回重试间隔不能为空"
            : edit.RetentionYears is null ? "保留年数不能为空"
            : edit.AuditRetentionYears is null ? "审计保留年数不能为空"
            : edit.MesTimeoutSeconds is null ? "MES 超时不能为空"
            : edit.ShiftStartHour is null ? "班次起点不能为空"
            : edit.ShiftLengthHours is null ? "班次时长不能为空"
            : null;

    public static SystemSettings Apply(SystemSettings latest, SettingsEdit edit, SettingsEdit loaded)
    {
        var target = latest.Clone();
        if (edit.ScanIntervalMs != loaded.ScanIntervalMs)
        {
            target.ScanIntervalMs = edit.ScanIntervalMs!.Value;
        }

        if (edit.WriteRetryCount != loaded.WriteRetryCount)
        {
            target.WriteRetryCount = edit.WriteRetryCount!.Value;
        }

        if (edit.WriteRetryDelayMs != loaded.WriteRetryDelayMs)
        {
            target.WriteRetryDelayMs = edit.WriteRetryDelayMs!.Value;
        }

        if (edit.RetentionYears != loaded.RetentionYears)
        {
            target.RetentionYears = edit.RetentionYears!.Value;
        }

        if (edit.AuditRetentionYears != loaded.AuditRetentionYears)
        {
            target.AuditRetentionYears = edit.AuditRetentionYears!.Value;
        }

        if (edit.MesTimeoutSeconds != loaded.MesTimeoutSeconds)
        {
            target.MesTimeoutSeconds = edit.MesTimeoutSeconds!.Value;
        }

        if (edit.ShiftStartHour != loaded.ShiftStartHour)
        {
            target.ShiftStartHour = edit.ShiftStartHour!.Value;
        }

        if (edit.ShiftLengthHours != loaded.ShiftLengthHours)
        {
            target.ShiftLengthHours = edit.ShiftLengthHours!.Value;
        }

        if (edit.CollectEnabled != loaded.CollectEnabled)
        {
            target.CollectEnabled = edit.CollectEnabled;
        }

        if (edit.MesEnabled != loaded.MesEnabled)
        {
            target.MesEnabled = edit.MesEnabled;
        }

        var endpoint = Normalize(edit.MesEndpoint);
        if (!string.Equals(endpoint, Normalize(loaded.MesEndpoint), StringComparison.Ordinal))
        {
            target.MesEndpoint = endpoint;
        }

        var alarmWebhook = Normalize(edit.AlarmWebhookUrl);
        if (!string.Equals(alarmWebhook, Normalize(loaded.AlarmWebhookUrl), StringComparison.Ordinal))
        {
            target.AlarmWebhookUrl = alarmWebhook;
        }

        return target;
    }

    public static string Summarize(SystemSettings settings)
        => $"scan={settings.ScanIntervalMs}ms; retry={settings.WriteRetryCount}×{settings.WriteRetryDelayMs}ms; retentionYears={settings.RetentionYears}; " +
           $"auditRetentionYears={settings.AuditRetentionYears}; collect={settings.CollectEnabled}; mes={settings.MesEnabled}; " +
           $"mesTimeout={settings.MesTimeoutSeconds}s; shift={settings.ShiftStartHour:00}:00×{settings.ShiftLengthHours}h; " +
           $"endpoint={settings.MesEndpoint}; alarmWebhook={settings.AlarmWebhookUrl}";

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsHttpUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
