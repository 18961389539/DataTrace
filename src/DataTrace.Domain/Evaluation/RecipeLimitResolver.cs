using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 把"点位默认限值 + 当前型号覆盖"解析成一组生效限值。
/// 判定链路、预警统计、过程能力都必须走这里，否则会出现"采集按新限值判废、
/// 报表按旧限值算 Cpk"这种口径不一致。
/// </summary>
public static class RecipeLimitResolver
{
    /// <summary>
    /// 解析点位在当前型号下的生效限值。
    /// 没有型号、型号已停用、或该点位没有覆盖行时，都回落到点位默认限值。
    /// </summary>
    public static TagLimits Resolve(TagDefinition tag, Recipe? recipe)
    {
        var fallback = TagLimits.From(tag);
        if (recipe is null || !recipe.Enabled)
        {
            return fallback;
        }

        var overrides = FindOverride(recipe, tag.Id);
        return overrides is null ? fallback : fallback.Override(TagLimits.From(overrides));
    }

    /// <summary>取某型号下某个点位的覆盖行；没有返回 null。</summary>
    public static RecipeLimit? FindOverride(Recipe recipe, int tagId)
        => recipe.Limits.FirstOrDefault(l => l.TagId == tagId);

    /// <summary>该型号实际覆盖了多少个点位（界面显示用）。</summary>
    public static int CountOverrides(Recipe recipe)
        => recipe.Limits.Select(l => l.TagId).Distinct().Count();
}
