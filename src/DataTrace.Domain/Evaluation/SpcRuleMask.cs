namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 点位上的判异规则开关（按位，位号 = <see cref="SpcRule"/> 的取值）。
/// </summary>
/// <remarks>
/// null = 全部规则：历史点位与"没单独配过"的点位都按全套判异走，升级后行为不变。
/// 需要按点位开关是因为有些点位天然带周期性（例如往复动作的位移），交替/趋势规则会长期误报；
/// 现场为了压掉噪声只能把整个点位停用，而那会把真正该看的超限也一起丢掉。
/// </remarks>
public static class SpcRuleMask
{
    /// <summary>已实现的规则，顺序即界面上的显示顺序。</summary>
    /// <remarks>必须声明在 <see cref="All"/> 之前：静态字段按声明顺序初始化，反了会聚合到 null。</remarks>
    public static IReadOnlyList<SpcRule> AllRules { get; } =
    [
        SpcRule.BeyondControlLimit,
        SpcRule.NineOnOneSide,
        SpcRule.SixMonotonic,
        SpcRule.FourteenAlternating,
        SpcRule.TwoOfThreeBeyondTwoSigma
    ];

    /// <summary>全部规则的位掩码（界面的"全选"与"取消全选"用它当基准）。</summary>
    public static int All { get; } = AllRules.Aggregate(0, (mask, rule) => mask | Bit(rule));

    /// <summary>这条规则在该点位上是否启用。null（没配过）= 全开。</summary>
    public static bool IsEnabled(int? mask, SpcRule rule) => mask is null || (mask.Value & Bit(rule)) != 0;

    /// <summary>
    /// 勾选/取消一条规则，返回新的掩码。
    /// </summary>
    /// <remarks>
    /// 传入 null（全开）时先展开成显式掩码再改动：否则"全开状态下关掉一条"会被
    /// 再读回来当成全开，等于这条永远关不掉。
    /// </remarks>
    public static int Set(int? mask, SpcRule rule, bool enabled)
    {
        var current = mask ?? All;
        return enabled ? current | Bit(rule) : current & ~Bit(rule);
    }

    private static int Bit(SpcRule rule) => 1 << (int)rule;
}