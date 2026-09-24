using DataTrace.Domain.Evaluation;

namespace DataTrace.Application.Reporting;

/// <summary>
/// 一段"规格限保持不变"的连续样本。
/// </summary>
/// <remarks>
/// 为什么必须分段而不是整窗算一个数：
/// Cp/Cpk 是"一组同规格数据对一条规格带"的比值，跨两套限值算出来的那个数没人能解释；
/// 而且改限值基本都和换刀、调机、换料同时发生，那个台阶会把移动极差抬起来，
/// 组内 σ 与控制限跟着失真，判异规则报的是"换型"而不是"过程异常"。
/// </remarks>
public sealed class ProcessCapabilitySegment
{
    /// <summary>1 起的段号，界面上标"段 2"。</summary>
    public int Number { get; init; }

    /// <summary>该段第一个样本在 <see cref="ProcessCapabilityReport.Samples"/> 中的下标。</summary>
    public int StartIndex { get; init; }

    public DateTime StartTime { get; init; }

    public DateTime EndTime { get; init; }

    public double? LowerLimit { get; init; }

    public double? UpperLimit { get; init; }

    /// <summary>
    /// 本段规格限是按当前配置估的，而不是采集当时落库的值 ——
    /// 限值那两列是后加的，之前的历史行只能是 null。
    /// </summary>
    public bool LimitsFromConfig { get; init; }

    /// <summary>该段样本数，同时也是它在 <see cref="ProcessCapabilityReport.Samples"/> 里占的长度。</summary>
    public int Count { get; init; }

    public required SpcSummary Summary { get; init; }

    /// <summary>判异结果，序号已换算成全窗口口径，可直接交给 <see cref="ProcessCapabilityReport.TimeAt"/>。</summary>
    public IReadOnlyList<SpcViolation> Violations { get; init; } = [];
}

/// <summary>单点位的控制图 + 过程能力分析结果，按规格限变化的位置分段。</summary>
public sealed class ProcessCapabilityReport
{
    public int TagId { get; init; }
    public string TagCode { get; init; } = "";
    public string TagName { get; init; } = "";
    public string? Unit { get; init; }

    /// <summary>
    /// 目标值取当前配置：判定那一刻的 target 没有随记录落库，
    /// 所以跨型号的窗口里这一项仍可能偏，只有规格限是逐段的。
    /// </summary>
    public double? TargetValue { get; init; }

    /// <summary>取数时点的活动型号编码；空串表示按点位默认限值。</summary>
    public string RecipeCode { get; init; } = "";

    /// <summary>按时间升序排列的全部采样值（跨段不切，趋势图与序号映射都靠它）。</summary>
    public IReadOnlyList<TrendPoint> Samples { get; init; } = [];

    /// <summary>至少一段；区间内没有采样数据时是一段 0 点的段。</summary>
    public IReadOnlyList<ProcessCapabilitySegment> Segments { get; init; } = [];

    /// <summary>窗口内规格限动过（因此结论必须分段看）。</summary>
    public bool SpecChangedInWindow => Segments.Count > 1;

    /// <summary>各段判异按全局采样序号合并，表格可以直接渲染。</summary>
    public IReadOnlyList<SpcViolation> Violations
        => Segments.SelectMany(s => s.Violations).OrderBy(v => v.StartIndex).ToList();

    /// <summary>某个采样序号落在第几段（1 起）。判异表在分段时要按段标注。</summary>
    public int SegmentNumberOf(int sampleIndex)
        => Segments.LastOrDefault(s => sampleIndex >= s.StartIndex)?.Number ?? 1;

    /// <summary>表头结论：各段里最差的可评估结论；一段都评不了才是"样本不足"。</summary>
    public SpcVerdict Verdict
    {
        get
        {
            var judged = Segments.Select(s => s.Summary.Verdict)
                .Where(v => v != SpcVerdict.InsufficientData)
                .ToList();
            return judged.Count > 0 ? judged.Min() : SpcVerdict.InsufficientData;
        }
    }

    /// <summary>把判异记录的采样序号映射回时间，方便现场定位。没有样本时返回 null。</summary>
    public DateTime? TimeAt(int sampleIndex)
        => sampleIndex >= 0 && sampleIndex < Samples.Count ? Samples[sampleIndex].Time : null;
}

public interface ISpcService
{
    /// <summary>
    /// 取某点位在区间内的采样值并完成过程能力分析。
    /// 点位不存在时返回 null（界面据此提示而不是显示一张空图）。
    /// </summary>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    /// <param name="take">最多取区间内<b>最新</b>的多少点；0 表示不限。与趋势图取同一个窗口，口径才一致。</param>
    Task<ProcessCapabilityReport?> GetProcessCapabilityAsync(
        int tagId,
        DateTime from,
        DateTime to,
        string? recipeCode = null,
        int take = 0,
        CancellationToken cancellationToken = default);
}
