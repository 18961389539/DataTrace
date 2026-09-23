using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Tests;

/// <summary>
/// 生效限值解析：点位默认限值 + 产品型号覆盖。
/// 覆盖的语义是<b>逐字段合并</b>（留空 = 沿用默认），不是"留空 = 不判"——
/// 漏填一个字段就把整项判定取消掉，风险太大。
/// </summary>
public class RecipeLimitResolverTests
{
    private static TagDefinition Tag() => new()
    {
        Id = 42,
        Code = "ST010_P1",
        Name = "压力",
        LowerLimit = 5,
        UpperLimit = 20,
        WarningLowerLimit = 7,
        WarningUpperLimit = 18,
        TargetValue = 12.5
    };

    private static Recipe RecipeWith(params RecipeLimit[] limits) => new()
    {
        Id = 1,
        Code = "A100",
        Name = "演示型号",
        Enabled = true,
        Limits = limits.ToList()
    };

    // ---------- 回落 ----------

    [Fact]
    public void Without_a_recipe_the_tag_keeps_its_own_limits()
    {
        var limits = RecipeLimitResolver.Resolve(Tag(), null);

        Assert.Equal(5d, limits.Lower);
        Assert.Equal(20d, limits.Upper);
        Assert.Equal(7d, limits.WarningLower);
        Assert.Equal(18d, limits.WarningUpper);
        Assert.Equal(12.5d, limits.Target);
    }

    [Fact]
    public void A_recipe_without_an_override_row_falls_back_to_the_tag()
    {
        var recipe = RecipeWith(new RecipeLimit { TagId = 999, UpperLimit = 1 });

        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        Assert.Equal(20d, limits.Upper);
    }

    [Fact]
    public void A_disabled_recipe_never_takes_effect()
    {
        var recipe = RecipeWith(new RecipeLimit { TagId = 42, UpperLimit = 16 });
        recipe.Enabled = false;

        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        // 宁可退回默认限值，也不要悄悄按一个已停用的型号判定。
        Assert.Equal(20d, limits.Upper);
    }

    // ---------- 逐字段合并 ----------

    [Fact]
    public void Only_the_filled_fields_are_overridden()
    {
        var recipe = RecipeWith(new RecipeLimit { TagId = 42, UpperLimit = 16 });

        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        Assert.Equal(16d, limits.Upper);
        // 其余字段留空 → 沿用默认，而不是变成"不判"。
        Assert.Equal(5d, limits.Lower);
        Assert.Equal(7d, limits.WarningLower);
        Assert.Equal(18d, limits.WarningUpper);
        Assert.Equal(12.5d, limits.Target);
    }

    [Fact]
    public void Every_field_can_be_overridden_independently()
    {
        var recipe = RecipeWith(new RecipeLimit
        {
            TagId = 42,
            LowerLimit = 6,
            UpperLimit = 15,
            WarningLowerLimit = 8,
            WarningUpperLimit = 13,
            TargetValue = 10.5
        });

        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        Assert.Equal(6d, limits.Lower);
        Assert.Equal(15d, limits.Upper);
        Assert.Equal(8d, limits.WarningLower);
        Assert.Equal(13d, limits.WarningUpper);
        Assert.Equal(10.5d, limits.Target);
    }

    [Fact]
    public void Overriding_a_warning_limit_can_disable_it_by_widening_it_to_the_spec()
    {
        // 型号把黄线放宽到与红线重合：预警带被压掉，但不是"不判"。
        var recipe = RecipeWith(new RecipeLimit { TagId = 42, WarningUpperLimit = 20 });

        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        Assert.Equal(20d, limits.WarningUpper);
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(limits, 19, isRequired: true));
        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(limits, 21, isRequired: true));
    }

    // ---------- 与判定的联动 ----------

    [Fact]
    public void The_same_value_can_pass_under_one_recipe_and_fail_under_another()
    {
        var tag = Tag();
        var strict = RecipeWith(new RecipeLimit { TagId = 42, UpperLimit = 16 });

        // 默认限值（规格上限 20、黄线 18）下 19 只是进入预警带，不算判废。
        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(tag, 19));
        Assert.False(LimitEvaluator.IsOutOfLimit(tag, 19));

        // 同一批数据切到 A100 后，规格上限收紧到 16，19 直接判废。
        Assert.Equal(LimitStatus.OutOfSpec,
            LimitEvaluator.Evaluate(RecipeLimitResolver.Resolve(tag, strict), 19, isRequired: true));
    }

    [Fact]
    public void Overriding_only_the_warning_limit_changes_the_band_but_not_the_verdict()
    {
        var tag = Tag();
        var recipe = RecipeWith(new RecipeLimit { TagId = 42, WarningUpperLimit = 15 });
        var limits = RecipeLimitResolver.Resolve(tag, recipe);

        // 默认黄线 18 → 19 算预警；型号收紧到 15 后 19 仍是预警，但依然不判废。
        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(limits, 19, isRequired: true));
        Assert.False(LimitEvaluator.IsOutOfLimit(tag, 19));
        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(limits, 21, isRequired: true));
    }

    // ---------- 辅助 ----------

    [Fact]
    public void Find_and_count_overrides()
    {
        var recipe = RecipeWith(
            new RecipeLimit { TagId = 42, UpperLimit = 16 },
            new RecipeLimit { TagId = 43, WarningUpperLimit = 45 });

        Assert.NotNull(RecipeLimitResolver.FindOverride(recipe, 42));
        Assert.Null(RecipeLimitResolver.FindOverride(recipe, 44));
        Assert.Equal(2, RecipeLimitResolver.CountOverrides(recipe));
        Assert.Equal(0, RecipeLimitResolver.CountOverrides(new Recipe()));
    }

    [Fact]
    public void Differs_from_defaults_detects_any_field_change()
    {
        var tag = Tag();
        var baseline = TagLimits.From(tag);

        Assert.False(baseline.DiffersFrom(TagLimits.From(tag)));
        Assert.False(baseline.DiffersFrom(new TagLimits(5, 20, 7, 18, 12.5)));
        Assert.True(baseline.DiffersFrom(new TagLimits(5, 16, 7, 18, 12.5)));
        Assert.True(baseline.DiffersFrom(new TagLimits(5, 20, 7, 18, 11)));
    }

    [Fact]
    public void Missing_required_value_is_still_decided_by_the_tag_not_the_recipe()
    {
        // IsRequired 是点位属性，型号覆盖不涉及它。
        var recipe = RecipeWith(new RecipeLimit { TagId = 42, UpperLimit = 16 });
        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(limits, null, isRequired: true));
        Assert.Equal(LimitStatus.None, LimitEvaluator.Evaluate(limits, null, isRequired: false));
    }

    [Fact]
    public void Consistency_check_passes_a_sane_set()
    {
        Assert.Null(TagLimits.From(Tag()).ConsistencyError());
    }

    [Fact]
    public void Consistency_check_rejects_bands_that_cross()
    {
        Assert.Equal("规格上限需要大于规格下限", new TagLimits(20, 20, null, null, null).ConsistencyError());
        Assert.Equal("预警上限需要大于预警下限", new TagLimits(0, 30, 18, 18, null).ConsistencyError());
    }

    [Fact]
    public void Consistency_check_keeps_the_warning_band_inside_the_spec_band()
    {
        Assert.Equal(
            "预警下限不能低于规格下限（黄线必须在红线内侧）",
            new TagLimits(10, 20, 5, 18, null).ConsistencyError());

        Assert.Equal(
            "预警上限不能高于规格上限（黄线必须在红线内侧）",
            new TagLimits(5, 20, 7, 25, null).ConsistencyError());
    }

    [Fact]
    public void Consistency_check_keeps_the_target_inside_the_spec_band()
    {
        // 型号限值对话框曾经漏掉这两条：同一组数字在工站配置页被拒、在对话框里却能保存。
        Assert.Equal("目标值不能低于规格下限", new TagLimits(5, 20, null, null, 1).ConsistencyError());
        Assert.Equal("目标值不能高于规格上限", new TagLimits(5, 20, null, null, 200).ConsistencyError());
    }

    [Fact]
    public void Recipe_override_cannot_sneak_past_the_rule()
    {
        // 点位自身是干净的，但型号把目标值顶到规格带外面 —— 校验必须打在合并后的生效值上。
        var recipe = RecipeWith(new RecipeLimit { TagId = 42, TargetValue = 200 });
        var limits = RecipeLimitResolver.Resolve(Tag(), recipe);

        Assert.Equal("目标值不能高于规格上限", limits.ConsistencyError());
    }
}
