using DataTrace.Domain.Evaluation;

namespace DataTrace.Application.Reporting;

/// <summary>单点位的控制图 + 过程能力分析结果。</summary>
public sealed class ProcessCapabilityReport
{
    public int TagId { get; init; }
    public string TagCode { get; init; } = "";
    public string TagName { get; init; } = "";
    public string? Unit { get; init; }

    public double? LowerLimit { get; init; }
    public double? UpperLimit { get; init; }
    public double? TargetValue { get; init; }

    /// <summary>计算能力指数所用的产品型号编码；空串表示按点位默认限值。</summary>
    public string RecipeCode { get; init; } = "";

    /// <summary>按时间升序排列的采样值。</summary>
    public IReadOnlyList<TrendPoint> Samples { get; init; } = [];

    public required SpcSummary Summary { get; init; }

    public IReadOnlyList<SpcViolation> Violations { get; init; } = [];

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
    Task<ProcessCapabilityReport?> GetProcessCapabilityAsync(
        int tagId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);
}
