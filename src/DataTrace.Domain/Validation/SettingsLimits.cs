using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Validation;

/// <summary>
/// 系统设置的取值范围：页面与仓储共用一份。
/// </summary>
/// <remarks>
/// 界面的 Min/Max 只是输入框的行为，脚本、MES 或历史脏数据可以直接写库；
/// 而几个取值各自的后果差别很大 —— 扫描间隔过小会把 PLC 通讯压垮，
/// 保留年数过小会直接删掉历史整月库与曲线文件，所以必须有一份能在仓储层拦下来的规则。
/// </remarks>
public static class SettingsLimits
{
    public const int MinScanIntervalMs = 20;
    public const int MaxScanIntervalMs = 5000;

    /// <summary>写回重试次数实际是"总尝试次数"（0 与 1 都只写一次）。</summary>
    public const int MinWriteRetryCount = 0;
    public const int MaxWriteRetryCount = 10;

    public const int MinWriteRetryDelayMs = 0;
    public const int MaxWriteRetryDelayMs = 10_000;

    public const int MinRetentionYears = 1;
    public const int MaxRetentionYears = 30;

    public const int MinMesTimeoutSeconds = 1;
    public const int MaxMesTimeoutSeconds = 300;

    /// <summary>仿真托盘间隔：小于 0.5 秒会把运行库写入量放大到产线节拍的数倍。</summary>
    public const int MinSimulatorIntervalMs = 500;
    public const int MaxSimulatorIntervalMs = 30_000;

    public const int MinSimulatorNgPercent = 0;
    public const int MaxSimulatorNgPercent = 100;

    /// <summary>托盘池数量：填 1 会让每轮都用同一个托盘码，报表里按托盘分不开批次。</summary>
    public const int MinSimulatorPalletPool = 1;
    public const int MaxSimulatorPalletPool = 500;

    public static string? ScanIntervalError(int value)
        => Range(value, MinScanIntervalMs, MaxScanIntervalMs, "扫描间隔(ms)");

    public static string? WriteRetryCountError(int value)
        => Range(value, MinWriteRetryCount, MaxWriteRetryCount, "写回重试次数");

    public static string? WriteRetryDelayError(int value)
        => Range(value, MinWriteRetryDelayMs, MaxWriteRetryDelayMs, "写回重试间隔(ms)");

    public static string? RetentionYearsError(int value)
        => Range(value, MinRetentionYears, MaxRetentionYears, "保留年数");

    public static string? MesTimeoutError(int value)
        => Range(value, MinMesTimeoutSeconds, MaxMesTimeoutSeconds, "MES 超时(秒)");

    public static string? SimulatorIntervalError(int value)
        => Range(value, MinSimulatorIntervalMs, MaxSimulatorIntervalMs, "仿真托盘间隔(ms)");

    public static string? SimulatorNgPercentError(int value)
        => Range(value, MinSimulatorNgPercent, MaxSimulatorNgPercent, "NG 比例(%)");

    public static string? SimulatorPalletPoolError(int value)
        => Range(value, MinSimulatorPalletPool, MaxSimulatorPalletPool, "托盘池数量");

    /// <summary>整份设置的第一个越界项；返回 null 表示都可用。</summary>
    public static string? Error(SystemSettings settings)
        => ScanIntervalError(settings.ScanIntervalMs)
           ?? WriteRetryCountError(settings.WriteRetryCount)
           ?? WriteRetryDelayError(settings.WriteRetryDelayMs)
           ?? RetentionYearsError(settings.RetentionYears)
           ?? MesTimeoutError(settings.MesTimeoutSeconds)
           ?? SimulatorIntervalError(settings.SimulatorIntervalMs)
           ?? SimulatorNgPercentError(settings.SimulatorNgPercent)
           ?? SimulatorPalletPoolError(settings.SimulatorPalletPool);

    /// <summary>
    /// 保留年限下"最新会被清理掉"的月份（用于保存前预告清理范围）。
    /// </summary>
    /// <remarks>
    /// 规则必须与 <c>RetentionHostedService</c> 一致：它以 <c>yyyyMM</c> 比较、删掉小于边界的整月库，
    /// 所以被删的最新一个月是边界月的前一个月。两边不一致时，界面预告的月份会和实际删的对不上。
    /// </remarks>
    public static DateTime NewestDeletedMonth(DateTime now, int retentionYears)
        => new DateTime(now.Year, now.Month, 1)
            .AddYears(-Math.Max(MinRetentionYears, retentionYears))
            .AddMonths(-1);

    private static string? Range(int value, int min, int max, string label)
        => value < min || value > max ? $"{label}必须在 {min}–{max} 之间" : null;
}
