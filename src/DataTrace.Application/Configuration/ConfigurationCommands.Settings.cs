using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Configuration;

public sealed record SaveSettingsCommand(
    int Id,
    int ScanIntervalMs,
    int WriteRetryCount,
    int WriteRetryDelayMs,
    int RetentionYears,
    int AuditRetentionYears,
    string CurveRootPath,
    string SpoolPath,
    string RuntimeDbPath,
    bool CollectEnabled,
    bool MesEnabled,
    string? MesEndpoint,
    int MesTimeoutSeconds,
    bool SimulatorAutoRun,
    int SimulatorIntervalMs,
    int SimulatorNgPercent,
    int SimulatorPalletPool,
    int? ActiveRecipeId,
    string? AlarmWebhookUrl)
{
    public static SaveSettingsCommand From(SystemSettings settings) => new(
        settings.Id,
        settings.ScanIntervalMs,
        settings.WriteRetryCount,
        settings.WriteRetryDelayMs,
        settings.RetentionYears,
        settings.AuditRetentionYears,
        settings.CurveRootPath,
        settings.SpoolPath,
        settings.RuntimeDbPath,
        settings.CollectEnabled,
        settings.MesEnabled,
        settings.MesEndpoint,
        settings.MesTimeoutSeconds,
        settings.SimulatorAutoRun,
        settings.SimulatorIntervalMs,
        settings.SimulatorNgPercent,
        settings.SimulatorPalletPool,
        settings.ActiveRecipeId,
        settings.AlarmWebhookUrl);

    public SystemSettings ToEntity() => new()
    {
        Id = Id,
        ScanIntervalMs = ScanIntervalMs,
        WriteRetryCount = WriteRetryCount,
        WriteRetryDelayMs = WriteRetryDelayMs,
        RetentionYears = RetentionYears,
        AuditRetentionYears = AuditRetentionYears,
        CurveRootPath = CurveRootPath,
        SpoolPath = SpoolPath,
        RuntimeDbPath = RuntimeDbPath,
        CollectEnabled = CollectEnabled,
        MesEnabled = MesEnabled,
        MesEndpoint = MesEndpoint,
        MesTimeoutSeconds = MesTimeoutSeconds,
        SimulatorAutoRun = SimulatorAutoRun,
        SimulatorIntervalMs = SimulatorIntervalMs,
        SimulatorNgPercent = SimulatorNgPercent,
        SimulatorPalletPool = SimulatorPalletPool,
        ActiveRecipeId = ActiveRecipeId,
        AlarmWebhookUrl = AlarmWebhookUrl
    };
}

/// <summary>
/// 实体重载：测试和仍拿着编辑模型的调用方可以继续传实体，仓储只接收命令。
/// </summary>
