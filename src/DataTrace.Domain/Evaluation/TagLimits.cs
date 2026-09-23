using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 一组生效限值（规格限 + 预警限 + 目标值）。
/// 引入它是为了让"判定按哪套限值"成为一个显式入参：
/// 点位自带默认限值，产品型号可以逐字段覆盖，判定与统计必须用同一套。
/// </summary>
public readonly record struct TagLimits(
    double? Lower,
    double? Upper,
    double? WarningLower,
    double? WarningUpper,
    double? Target)
{
    /// <summary>点位自身的默认限值。</summary>
    public static TagLimits From(TagDefinition tag)
        => new(tag.LowerLimit, tag.UpperLimit, tag.WarningLowerLimit, tag.WarningUpperLimit, tag.TargetValue);

    /// <summary>型号覆盖行提供的限值。空字段表示"不覆盖"。</summary>
    public static TagLimits From(RecipeLimit limit)
        => new(limit.LowerLimit, limit.UpperLimit, limit.WarningLowerLimit, limit.WarningUpperLimit, limit.TargetValue);

    /// <summary>是否配置了任意一级限值。目标值不算限值。</summary>
    public bool HasAny
        => Lower is not null || Upper is not null || WarningLower is not null || WarningUpper is not null;

    /// <summary>
    /// 逐字段合并覆盖值：<b>覆盖行留空的字段沿用默认值</b>。
    /// 注意这与曲线判据的"留空 = 不判"是两套语义：这里留空是"不改这一项"，
    /// 因为漏填就把某个判定项整体取消，风险太大。
    /// </summary>
    public TagLimits Override(TagLimits overrides) => new(
        overrides.Lower ?? Lower,
        overrides.Upper ?? Upper,
        overrides.WarningLower ?? WarningLower,
        overrides.WarningUpper ?? WarningUpper,
        overrides.Target ?? Target);

    /// <summary>与另一套限值是否存在任何差异 —— 界面用来标记"该点位被本型号覆盖"。</summary>
    public bool DiffersFrom(TagLimits other)
        => Lower != other.Lower
           || Upper != other.Upper
           || WarningLower != other.WarningLower
           || WarningUpper != other.WarningUpper
           || Target != other.Target;

    /// <summary>
    /// 这套限值是否自洽：黄线必须落在红线内侧，目标值必须落在规格带内。
    /// 返回 null 表示通过，否则返回可以直接给工程师看的那句话。
    /// <para>
    /// 这条规则必须只有这一份。它曾经在工站配置页和型号限值对话框里各写了一遍，
    /// 结果对话框那份漏掉了目标值两条 —— 同一组数字一边拒绝一边放行，
    /// 而 <see cref="LimitEvaluator"/> 只在判定时默默收敛，配置里就留下了自相矛盾的限值。
    /// </para>
    /// </summary>
    public string? ConsistencyError()
    {
        if (Upper is { } upper && Lower is { } lower && upper <= lower)
        {
            return "规格上限需要大于规格下限";
        }

        if (WarningUpper is { } warningUpper && WarningLower is { } warningLower && warningUpper <= warningLower)
        {
            return "预警上限需要大于预警下限";
        }

        if (WarningLower is { } warnLow && Lower is { } specLow && warnLow < specLow)
        {
            return "预警下限不能低于规格下限（黄线必须在红线内侧）";
        }

        if (WarningUpper is { } warnHigh && Upper is { } specHigh && warnHigh > specHigh)
        {
            return "预警上限不能高于规格上限（黄线必须在红线内侧）";
        }

        if (Target is { } target)
        {
            if (Lower is { } targetLow && target < targetLow)
            {
                return "目标值不能低于规格下限";
            }

            if (Upper is { } targetHigh && target > targetHigh)
            {
                return "目标值不能高于规格上限";
            }
        }

        return null;
    }
}
