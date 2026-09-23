namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 过程能力结论。<see cref="InsufficientData"/> 表示"算得出数字但不足以据此下结论"，
/// 此时应当看 <see cref="SpcSummary.Note"/> 里的原因，而不是盯 Cpk 数值。
/// </summary>
public enum SpcVerdict
{
    /// <summary>样本不足、缺规格限或波动为 0，无法评估。</summary>
    InsufficientData = 0,

    /// <summary>Cpk &lt; 1.00，过程能力不足。</summary>
    Incapable = 1,

    /// <summary>1.00 ≤ Cpk &lt; 1.33，过程能力勉强，需要关注。</summary>
    Marginal = 2,

    /// <summary>Cpk ≥ 1.33，过程能力达标。</summary>
    Capable = 3
}

/// <summary>判异准则。编号沿用 Nelson / Western Electric 规则的顺序。</summary>
public enum SpcRule
{
    /// <summary>规则 1：单点超出 ±3σ 控制限。</summary>
    BeyondControlLimit = 1,

    /// <summary>规则 2：连续 9 点落在中心线同一侧。</summary>
    NineOnOneSide = 2,

    /// <summary>规则 3：连续 6 点单调递增或递减（趋势）。</summary>
    SixMonotonic = 3,

    /// <summary>规则 4：连续 14 点上下交替（系统性振荡）。</summary>
    FourteenAlternating = 4,

    /// <summary>规则 5：连续 3 点中有 2 点同侧超出 ±2σ。</summary>
    TwoOfThreeBeyondTwoSigma = 5
}

/// <summary>
/// 单值-移动极差（I-MR）控制图统计量 + 过程能力指数。
/// 所有能力指数在无法计算时为 null，绝不返回 NaN 或 Infinity —— 界面直接显示"无法评估"。
/// </summary>
public sealed record SpcSummary
{
    /// <summary>参与计算的样本数。</summary>
    public int Count { get; init; }

    public double Mean { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    /// <summary>总体标准差（样本标准差，除以 n-1），用于 Ppk。</summary>
    public double OverallStdDev { get; init; }

    /// <summary>组内标准差 = 移动极差均值 / d2，用于 Cp/Cpk 与控制限。</summary>
    public double WithinStdDev { get; init; }

    /// <summary>移动极差均值（n=2）。</summary>
    public double MovingRangeMean { get; init; }

    /// <summary>移动极差的上控制限（D4 × MR̄）。</summary>
    public double MovingRangeUpperLimit { get; init; }

    /// <summary>X 图中心线（= 均值）。</summary>
    public double CenterLine { get; init; }

    /// <summary>X 图上控制限（均值 + 2.66 × MR̄）。</summary>
    public double UpperControlLimit { get; init; }

    /// <summary>X 图下控制限（均值 - 2.66 × MR̄）。</summary>
    public double LowerControlLimit { get; init; }

    public double? Cp { get; init; }

    public double? Cpu { get; init; }

    public double? Cpl { get; init; }

    public double? Cpk { get; init; }

    public double? Pp { get; init; }

    public double? Ppk { get; init; }

    /// <summary>是否至少配置了一侧规格限。</summary>
    public bool HasSpecLimits { get; init; }

    public SpcVerdict Verdict { get; init; }

    /// <summary>结论依据的说明；<see cref="SpcVerdict.InsufficientData"/> 时必填。</summary>
    public string? Note { get; init; }

    /// <summary>均值相对规格中心/目标值的偏移量（无量纲，单位是组内 σ 的倍数）。</summary>
    public double? MeanOffsetInSigma { get; init; }
}

/// <summary>一条被触发的判异记录，覆盖连续的一段采样点。</summary>
public sealed record SpcViolation
{
    public required SpcRule Rule { get; init; }

    /// <summary>可直接展示给现场的说明。</summary>
    public required string Description { get; init; }

    /// <summary>起始采样序号（0 起，含）。</summary>
    public int StartIndex { get; init; }

    /// <summary>结束采样序号（含）。</summary>
    public int EndIndex { get; init; }

    public int Count => EndIndex - StartIndex + 1;
}
