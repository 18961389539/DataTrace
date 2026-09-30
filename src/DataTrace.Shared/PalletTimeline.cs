namespace DataTrace.Shared;

/// <summary>
/// 托盘全链路时序：一站到下一站之间那一段，以及整条链路的总时长。
/// </summary>
/// <remarks>
/// 抽成纯函数是为了能单独断言边界（首站没有上一站、中间缺站、时钟回拨），
/// 这些恰恰是最容易把"缺失"显示成"很快"的地方。
/// </remarks>
public static class PalletTimeline
{
    /// <summary>
    /// 这一站的触发时刻距上一站完成过去了多久。
    /// </summary>
    /// <remarks>
    /// 两端任一缺失就返回 null，**不回落成 0**：0 会被读成"瞬间流转"，
    /// 把一段缺失说成了"走得很快"。两端时刻倒挂（时钟回拨、跨设备对时）时同样返回 null。
    /// 中间跳过若干站时，算出来的是跨过它们之后的真实间隔 —— 这正是排查"这件为什么慢了"要看的那一段。
    /// </remarks>
    public static TimeSpan? Transit(DateTime? previousComplete, DateTime? currentTrigger)
    {
        if (previousComplete is not { } from || currentTrigger is not { } to || to < from)
        {
            return null;
        }

        return to - from;
    }

    /// <summary>整条链路的时长：首站触发到末站完成。</summary>
    public static TimeSpan? Total(DateTime? firstTrigger, DateTime? lastComplete)
        => Transit(firstTrigger, lastComplete);

    /// <summary>
    /// 某一段占整条链路的比例，用来画时序条。
    /// </summary>
    /// <remarks>总时长缺失或为零时返回 0，而不是抛异常：画不出比例只是不好看，不该让整页渲染失败。</remarks>
    public static double Share(TimeSpan? part, TimeSpan? total)
    {
        if (part is not { } value || total is not { } whole || whole <= TimeSpan.Zero)
        {
            return 0;
        }

        return Math.Clamp(value.TotalMilliseconds / whole.TotalMilliseconds, 0, 1);
    }
}
