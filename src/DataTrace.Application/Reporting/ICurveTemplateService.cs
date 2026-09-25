using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Application.Reporting;

/// <summary>某条曲线某个序列的波形基线分析结果。</summary>
public sealed class CurveBaselineReport
{
    public int CurveDefinitionId { get; init; }
    public string CurveCode { get; init; } = "";
    public string CurveName { get; init; } = "";

    /// <summary>实际参与分析的序列名（调用方留空时会自动落到主序列）。</summary>
    public string SeriesName { get; init; } = "";
    public SeriesRole Role { get; init; }
    public string? Unit { get; init; }

    /// <summary>区间内该序列的样本总数（含不合格、含型号不匹配的），不受打分窗口上限影响。</summary>
    public int TotalSampleCount { get; init; }

    /// <summary>因型号与本型号范围（当前型号 + 它改码前的编码）不一致而被排除的样本数。</summary>
    public int MismatchedRecipeCount { get; init; }

    /// <summary>建立基线时使用的合格样本数。</summary>
    public int BaselineSampleCount { get; init; }

    /// <summary>建立基线时生效的产品型号编码；空串表示按未选型号（默认限值）判定。</summary>
    public string RecipeCode { get; init; } = "";

    /// <summary>区间内属于本型号但不合格的样本数，用于对照偏离分是否真的抓到了不良。</summary>
    public int NgCount { get; init; }

    public required CurveTemplate Template { get; init; }

    /// <summary>最近若干条样本的打分，按时间升序。</summary>
    public IReadOnlyList<CurveTemplateScore> Recent { get; init; } = [];

    public int AbnormalCount => Recent.Count(s => s.Verdict == CurveTemplateVerdict.Abnormal);

    public int SuspiciousCount => Recent.Count(s => s.Verdict == CurveTemplateVerdict.Suspicious);

    /// <summary>模型无法建立（无样本 / 型号不匹配 / 全维度零波动）时的说明；正常时为空。</summary>
    public string? EmptyReason { get; init; }

    public required CurveShadowComparison Shadow { get; init; }
}

/// <summary>
/// 影子模式的对照结果：把"偏离分怎么判"和"实际怎么判"交叉起来。
/// </summary>
/// <remarks>
/// 这是决定这套算法能不能从"只记录"升级为"预警"的核心依据。
/// 关键看的不是命中多少，而是 <see cref="FalsePositive"/>（误报）——
/// 产线上一个天天乱响的报警会让人把整个面板关掉，那时再准也没用。
/// </remarks>
public sealed class CurveShadowComparison
{
    /// <summary>区间内拿到了偏离结论的样本数（基线不可用的不算）。</summary>
    public int Checked { get; init; }

    /// <summary>偏离异常 且 实际判废 —— 算法真的抓到了东西。</summary>
    public int TruePositive { get; init; }

    /// <summary>偏离异常 但 实际合格 —— 误报。</summary>
    public int FalsePositive { get; init; }

    /// <summary>偏离正常 但 实际判废 —— 漏报，说明偏离分抓不到这类不良。</summary>
    public int FalseNegative { get; init; }

    /// <summary>偏离正常 且 实际合格。</summary>
    public int TrueNegative { get; init; }

    /// <summary>报警中真正有问题的比例。分母为 0 时返回 null（不编造数字）。</summary>
    public double? Precision => TruePositive + FalsePositive > 0
        ? (double)TruePositive / (TruePositive + FalsePositive)
        : null;

    /// <summary>实际不良中被抓到的比例。分母为 0 时返回 null。</summary>
    public double? Recall => TruePositive + FalseNegative > 0
        ? (double)TruePositive / (TruePositive + FalseNegative)
        : null;

    /// <summary>误报占全部已检查样本的比例。分母为 0 时返回 null。</summary>
    public double? FalsePositiveRate => Checked > 0 ? (double)FalsePositive / Checked : null;
}

public interface ICurveTemplateService
{
    /// <summary>
    /// 建立波形基线并给最近的样本打分。
    /// 曲线定义或序列不存在时返回 null（界面据此提示，而不是显示一张空图）。
    /// </summary>
    /// <param name="seriesName">序列名；留空表示主序列（优先 Y 角色）。</param>
    /// <param name="recentCount">参与打分的最近样本条数。</param>
    /// <param name="maxSamples">建立基线的样本上限（取区间内最新的这么多条）。</param>
    Task<CurveBaselineReport?> GetBaselineAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int recentCount = 30,
        int maxSamples = 2000,
        CancellationToken cancellationToken = default);
}
