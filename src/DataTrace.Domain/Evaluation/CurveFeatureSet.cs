namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 单条曲线序列的特征集合。全部为纯数值，可直接字段化落库、参与 SQL 聚合，
/// 也可直接作为后续 SPC / 异常检测的输入特征。
/// </summary>
/// <remarks>
/// 约定：横轴为采样序号（0 起），因此"斜率"单位是 工程单位/采样点。
/// 空序列返回全 0 特征；常值序列的范围为 0，相关比值特征按 0 处理，不抛异常。
/// </remarks>
public sealed record CurveFeatureSet
{
    /// <summary>采样点数。</summary>
    public int PointCount { get; init; }

    /// <summary>最小值。</summary>
    public double Min { get; init; }

    /// <summary>最小值所在采样序号。</summary>
    public int MinIndex { get; init; }

    /// <summary>峰值（最大值）。</summary>
    public double Peak { get; init; }

    /// <summary>峰值所在采样序号。</summary>
    public int PeakIndex { get; init; }

    /// <summary>算术平均值。</summary>
    public double Mean { get; init; }

    /// <summary>总体标准差（除以 N）。</summary>
    public double StdDev { get; init; }

    /// <summary>梯形法积分面积（对采样序号）。</summary>
    public double Area { get; init; }

    /// <summary>上升段 [起点, 峰值] 的最小二乘斜率。</summary>
    public double RiseSlope { get; init; }

    /// <summary>保压/回落段 [峰值, 末点] 的最小二乘斜率。</summary>
    public double HoldSlope { get; init; }

    /// <summary>起升点序号：首个达到 min + 10% 量程的采样点。</summary>
    public int RiseIndex { get; init; }

    /// <summary>起升跨度：峰值序号 - 起升点序号，反映爬升快慢。</summary>
    public int RiseSpan { get; init; }

    /// <summary>回落比例：(峰值 - 末值) / 量程，量程为 0 时取 0。</summary>
    public double FallRatio { get; init; }

    /// <summary>相邻采样点的最大绝对跳变，用于捕捉冲击与突变。</summary>
    public double MaxStep { get; init; }

    /// <summary>穿越均值线的次数，用于捕捉振荡。</summary>
    public int Oscillations { get; init; }
}
