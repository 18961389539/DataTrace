using DataTrace.Domain.Constants;

namespace DataTrace.Domain.Validation;

/// <summary>
/// 工站配置的硬性上限与约定：页面、仓储、采集三层共用同一份，避免各写一套判断后慢慢走样。
/// </summary>
public static class StationConfigLimits
{
    /// <summary>
    /// 托盘码长度上限（字符）。<see cref="PalletCodeValidator"/> 只认 1–64 个字符，
    /// 配得再长也只会白白多读几百字，还把"长度配错"表现成"托盘码非法"。
    /// </summary>
    public const int MaxPalletCodeLength = 64;

    public const int MinCurvePoints = 2;

    /// <summary>
    /// 单条曲线的点数上限。
    /// </summary>
    /// <remarks>
    /// 点数直接决定单次采集的读取量：10000 点 × 2 字/点 ≈ 20 次串行读取（按 960 字分块），
    /// 再多会把工站采集周期拉到秒级，月库里的波形体积也会失控。
    /// </remarks>
    public const int MaxCurvePoints = 10000;

    /// <summary>触发与回写共用一个寄存器：这个区间是采集完成后要写回去的响应码。</summary>
    public static bool IsWriteBackCode(short value)
        => value is >= ResultCodes.Success and <= ResultCodes.ArchiveFailed;

    /// <summary>
    /// 触发值校验：触发与回写共用同一个寄存器。
    /// </summary>
    /// <remarks>
    /// 触发值一旦落在回写码区间，采集写完响应码后寄存器仍等于触发值，
    /// 扫描循环会一个周期采一次（实测：约 2 秒内同一托盘落库 3 条以上记录，
    /// 首站还会反复分配序列号并把上一会话标异常）。返回 null 表示可用。
    /// </remarks>
    public static string? TriggerValueError(short value) => value switch
    {
        0 => "触发值不能为 0：上电或复位后寄存器本来就是 0，会让工站在开机瞬间误触发一次",
        _ when IsWriteBackCode(value)
            => $"触发值不能取 {value}：2–{ResultCodes.ArchiveFailed} 是采集完成后回写的响应码，两者相同会让触发位永远清不掉、工站被反复触发",
        _ => null
    };

    public static string? PointCountError(int pointCount)
        => pointCount < MinCurvePoints ? $"曲线点数至少为 {MinCurvePoints}"
            : pointCount > MaxCurvePoints ? $"曲线点数不能超过 {MaxCurvePoints}（点数过多会把单次采集拆成几十次串行读取）"
            : null;

    public static string? PalletCodeLengthError(int length)
        => length < 1 ? "托盘码长度必须大于 0"
            : length > MaxPalletCodeLength ? $"托盘码长度不能超过 {MaxPalletCodeLength}（托盘码本身最多 64 个字符）"
            : null;
}
