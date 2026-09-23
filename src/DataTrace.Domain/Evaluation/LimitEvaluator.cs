using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 点位限值判定。区间为闭区间（等于边界算合格）。
/// 分三级限值：规格限（红线，超出判废）、预警限（黄线，只提示）、目标值（不参与判定）。
/// </summary>
/// <remarks>
/// 判定吃的是 <see cref="TagLimits"/>（生效限值），而不是点位自身的字段 ——
/// 这样切换产品型号时不需要改动点位定义。要按点位默认限值判定，
/// 用 <see cref="TagLimits.From(TagDefinition)"/> 取一组再传进来。
/// </remarks>
public static class LimitEvaluator
{
    /// <summary>单点位三态判定。只有 <see cref="LimitStatus.OutOfSpec"/> 意味着判废。</summary>
    public static LimitStatus Evaluate(TagLimits limits, double? numeric, bool isRequired)
    {
        if (numeric is null)
        {
            // 空值无法判限，是否算异常完全取决于该点位是否必填（与限值无关）。
            return isRequired ? LimitStatus.OutOfSpec : LimitStatus.None;
        }

        var value = numeric.Value;

        // 规格限优先：一旦越界直接判废，不再看预警带。
        if (limits.Lower is { } lower && value < lower)
        {
            return LimitStatus.OutOfSpec;
        }

        if (limits.Upper is { } upper && value > upper)
        {
            return LimitStatus.OutOfSpec;
        }

        // 预警带只朝规格带内侧生效。若现场把黄线配到了红线之外，
        // 这里按红线收敛，避免出现"既不超规格又算预警"的矛盾状态。
        if (limits.WarningLower is { } warningLower && value < EffectiveLower(limits, warningLower))
        {
            return LimitStatus.Warning;
        }

        if (limits.WarningUpper is { } warningUpper && value > EffectiveUpper(limits, warningUpper))
        {
            return LimitStatus.Warning;
        }

        return limits.HasAny ? LimitStatus.InSpec : LimitStatus.None;
    }

    /// <summary>按点位默认限值判定（不含型号覆盖）。</summary>
    public static LimitStatus Evaluate(TagDefinition tag, double? numeric)
        => Evaluate(TagLimits.From(tag), numeric, tag.IsRequired);

    /// <summary>是否超出规格限（规格限越界，或必填点位取空）。</summary>
    public static bool IsOutOfLimit(TagDefinition tag, double? numeric)
        => Evaluate(tag, numeric) == LimitStatus.OutOfSpec;

    /// <summary>预警下界的实际生效值：不严于规格下限。</summary>
    private static double EffectiveLower(TagLimits limits, double warningLower)
        => limits.Lower is { } spec ? Math.Max(warningLower, spec) : warningLower;

    /// <summary>预警上界的实际生效值：不宽于规格上限。</summary>
    private static double EffectiveUpper(TagLimits limits, double warningUpper)
        => limits.Upper is { } spec ? Math.Min(warningUpper, spec) : warningUpper;

    public static Judgement Combine(IEnumerable<Judgement> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            return Judgement.None;
        }

        if (list.Any(x => x == Judgement.Ng))
        {
            return Judgement.Ng;
        }

        if (list.All(x => x == Judgement.None))
        {
            return Judgement.None;
        }

        return Judgement.Ok;
    }
}
