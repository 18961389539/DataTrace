using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 参与波形基线比对的维度。每个维度对应 <see cref="CurveFeature"/> 上的一个字段。
/// </summary>
/// <remarks>
/// 故意不含 <c>PointCount</c>：它由曲线定义决定，所有样本恒等，不含任何过程信息。
/// </remarks>
public enum CurveFeatureDimension
{
    /// <summary>波形最小值。</summary>
    Min = 0,

    /// <summary>波形峰值。</summary>
    Peak = 1,

    /// <summary>算术均值。</summary>
    Mean = 2,

    /// <summary>波形自身波动（序列内标准差）。</summary>
    StdDev = 3,

    /// <summary>梯形积分面积。</summary>
    Area = 4,

    /// <summary>上升段斜率。</summary>
    RiseSlope = 5,

    /// <summary>保压段斜率。</summary>
    HoldSlope = 6,

    /// <summary>回落比例。</summary>
    FallRatio = 7,

    /// <summary>相邻点最大跳变。</summary>
    MaxStep = 8,

    /// <summary>穿越均值线次数。</summary>
    Oscillations = 9,

    /// <summary>起升跨度（峰值序号 - 起升序号）。</summary>
    RiseSpan = 10,

    /// <summary>峰值所在采样序号（相位信息）。</summary>
    PeakIndex = 11,

    /// <summary>最小值所在采样序号。</summary>
    MinIndex = 12,

    /// <summary>起升点序号。</summary>
    RiseIndex = 13
}

/// <summary>
/// 维度枚举的取值与元信息。全部为纯函数，便于在测试里逐个维度核对。
/// </summary>
public static class CurveFeatureDimensions
{
    /// <summary>参与比对的全部维度，顺序即界面展示顺序（幅值 → 形状 → 相位）。</summary>
    public static readonly IReadOnlyList<CurveFeatureDimension> All =
    [
        CurveFeatureDimension.Min,
        CurveFeatureDimension.Peak,
        CurveFeatureDimension.Mean,
        CurveFeatureDimension.StdDev,
        CurveFeatureDimension.Area,
        CurveFeatureDimension.RiseSlope,
        CurveFeatureDimension.HoldSlope,
        CurveFeatureDimension.FallRatio,
        CurveFeatureDimension.MaxStep,
        CurveFeatureDimension.Oscillations,
        CurveFeatureDimension.RiseSpan,
        CurveFeatureDimension.PeakIndex,
        CurveFeatureDimension.MinIndex,
        CurveFeatureDimension.RiseIndex
    ];

    /// <summary>从特征行读出某个维度的值。</summary>
    public static double Read(CurveFeature feature, CurveFeatureDimension dimension) => dimension switch
    {
        CurveFeatureDimension.Min => feature.Min,
        CurveFeatureDimension.Peak => feature.Peak,
        CurveFeatureDimension.Mean => feature.Mean,
        CurveFeatureDimension.StdDev => feature.StdDev,
        CurveFeatureDimension.Area => feature.Area,
        CurveFeatureDimension.RiseSlope => feature.RiseSlope,
        CurveFeatureDimension.HoldSlope => feature.HoldSlope,
        CurveFeatureDimension.FallRatio => feature.FallRatio,
        CurveFeatureDimension.MaxStep => feature.MaxStep,
        CurveFeatureDimension.Oscillations => feature.Oscillations,
        CurveFeatureDimension.RiseSpan => feature.RiseSpan,
        CurveFeatureDimension.PeakIndex => feature.PeakIndex,
        CurveFeatureDimension.MinIndex => feature.MinIndex,
        CurveFeatureDimension.RiseIndex => feature.RiseIndex,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, "未知的波形特征维度")
    };

    /// <summary>维度中文名，供界面与导出直接使用。</summary>
    public static string Label(CurveFeatureDimension dimension) => dimension switch
    {
        CurveFeatureDimension.Min => "最小值",
        CurveFeatureDimension.Peak => "峰值",
        CurveFeatureDimension.Mean => "均值",
        CurveFeatureDimension.StdDev => "波形波动",
        CurveFeatureDimension.Area => "面积",
        CurveFeatureDimension.RiseSlope => "上升段斜率",
        CurveFeatureDimension.HoldSlope => "保压段斜率",
        CurveFeatureDimension.FallRatio => "回落比例",
        CurveFeatureDimension.MaxStep => "最大单步跳变",
        CurveFeatureDimension.Oscillations => "振荡次数",
        CurveFeatureDimension.RiseSpan => "爬升跨度",
        CurveFeatureDimension.PeakIndex => "峰值位置",
        CurveFeatureDimension.MinIndex => "最小值位置",
        CurveFeatureDimension.RiseIndex => "起升位置",
        _ => dimension.ToString()
    };

    /// <summary>
    /// 该维度是否为连续量。
    /// 采样序号与计数是离散量：它们零波动时（例如峰值总在第 25 点）不代表过程受控，
    /// 因此不参与"零波动维度的常量偏离"判定，否则会产生大量误报。
    /// </summary>
    public static bool IsContinuous(CurveFeatureDimension dimension) => dimension switch
    {
        CurveFeatureDimension.Oscillations => false,
        CurveFeatureDimension.RiseSpan => false,
        CurveFeatureDimension.PeakIndex => false,
        CurveFeatureDimension.MinIndex => false,
        CurveFeatureDimension.RiseIndex => false,
        _ => true
    };
}

/// <summary>单个维度的基线统计量。中心与离散度用稳健估计，避免历史里的不良样本把基线拉偏。</summary>
public sealed record CurveDimensionBaseline
{
    public required CurveFeatureDimension Dimension { get; init; }

    /// <summary>参与统计的样本数。</summary>
    public int Count { get; init; }

    /// <summary>稳健中心：中位数。打分的基准。</summary>
    public double Center { get; init; }

    /// <summary>稳健离散度：1.4826 × MAD（中位数绝对偏差）。打分的基准，量纲与 z 分数一致。</summary>
    public double Sigma { get; init; }

    /// <summary>算术均值。<b>仅供展示</b>，打分不使用 —— 它会被历史里的离群样本拉偏。</summary>
    public double Mean { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    /// <summary>按 3σ 稳健规则识别出的离群样本数。仅作提示，不参与计算。</summary>
    public int OutlierCount { get; init; }

    /// <summary>该维度在所有样本里恒等（稳健离散度为 0）。此时不参与 z 分数计算。</summary>
    public bool IsConstant { get; init; }

    /// <summary>
    /// 零波动维度的常量偏离容差：<c>max(1e-9, |中心| × 1%)</c>。
    /// 用相对容差而不是绝对零，避免浮点噪声把"没变"判成"变了"。
    /// </summary>
    public double ConstantTolerance => Math.Max(1e-9, Math.Abs(Center) * 0.01);

    /// <summary>该维度是否具备统计意义（有样本、非零波动）。</summary>
    public bool IsScorable => Count > 1 && !IsConstant && Sigma > 0;

    /// <summary>基线的上下限（中心 ± 3σ），供界面画容差带。</summary>
    public double LowerBound => Center - 3 * Sigma;

    public double UpperBound => Center + 3 * Sigma;
}

/// <summary>
/// 波形基线模板：某个（曲线定义 + 序列 + 型号）下"正常长什么样"。
/// </summary>
public sealed record CurveTemplate
{
    /// <summary>建模板所用的合格样本数。</summary>
    public int SampleCount { get; init; }

    /// <summary>可参与打分的维度数（样本充足且非零波动）。</summary>
    public int ScorableDimensionCount { get; init; }

    /// <summary>模板是否可靠：样本数达到 <see cref="CurveTemplateBuilder.MinimumReliableSamples"/>。</summary>
    public bool IsReliable { get; init; }

    /// <summary>模板说明；不可靠时写明原因。</summary>
    public string? Note { get; init; }

    public IReadOnlyList<CurveDimensionBaseline> Dimensions { get; init; } = [];

    public CurveDimensionBaseline? this[CurveFeatureDimension dimension]
        => Dimensions.FirstOrDefault(d => d.Dimension == dimension);
}

/// <summary>单个维度相对基线的偏离。</summary>
public sealed record CurveTemplateDeviation
{
    public required CurveFeatureDimension Dimension { get; init; }

    public double Value { get; init; }

    public double BaselineCenter { get; init; }

    public double BaselineSigma { get; init; }

    /// <summary>标准化偏离量。零波动维度或基线无效时为 null（不编造数字）。</summary>
    public double? ZScore { get; init; }

    /// <summary>零波动维度的实测值与常量值不符。</summary>
    public bool IsConstantBreach { get; init; }

    /// <summary>该维度是否被判定为显著偏离。</summary>
    public bool IsDeviating => IsConstantBreach || (ZScore is { } z && Math.Abs(z) >= CurveTemplateMatcher.SuspiciousZ);
}

/// <summary>波形基线的比对结论。</summary>
public enum CurveTemplateVerdict
{
    /// <summary>模板本身不可靠（样本不足），不下结论。</summary>
    InsufficientBaseline = 0,

    /// <summary>与基线一致。</summary>
    Normal = 1,

    /// <summary>轻度偏离，需要关注。</summary>
    Suspicious = 2,

    /// <summary>显著偏离，疑似异常波形。</summary>
    Abnormal = 3
}

/// <summary>一条曲线相对基线的比对结果。</summary>
public sealed record CurveTemplateScore
{
    public long CurveRecordId { get; init; }

    public DateTime Time { get; init; }

    public string PalletCode { get; init; } = "";

    /// <summary>该曲线所属采集记录的判定，便于对照"偏离分是否抓到了真实不良"。</summary>
    public bool IsNg { get; init; }

    /// <summary>
    /// 综合偏离分：各维度 z 分数的均方根（RMS）。
    /// 在对角协方差假设下等价于马氏距离，量纲无关，可横向比较。
    /// </summary>
    public double RmsZ { get; init; }

    /// <summary>单一维度上的最大绝对 z 分数。</summary>
    public double MaxAbsZ { get; init; }

    /// <summary>偏离最大的维度；无维度可打分时为 null。</summary>
    public CurveFeatureDimension? WorstDimension { get; init; }

    /// <summary>零波动维度被打破的次数。</summary>
    public int ConstantBreachCount { get; init; }

    public CurveTemplateVerdict Verdict { get; init; }

    /// <summary>参与打分的维度数。</summary>
    public int ScoredDimensionCount { get; init; }

    /// <summary>按绝对 z 分数降序排列的显著偏离明细（可能为空）。</summary>
    public IReadOnlyList<CurveTemplateDeviation> Deviations { get; init; } = [];
}
