using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;

namespace DataTrace.Tests;

public class DomainRuleTests
{
    // ---------- 托盘码校验 ----------

    [Theory]
    [InlineData("P0001")]
    [InlineData("abc-123_XYZ")]
    [InlineData("1")]
    [InlineData("PALLET-2026-09-19")]
    public void Pallet_code_accepts_legal_values(string code)
    {
        Assert.True(PalletCodeValidator.IsValid(code, out var error));
        Assert.Equal("", error);
    }

    [Fact]
    public void Pallet_code_accepts_exactly_64_chars_but_rejects_65()
    {
        Assert.True(PalletCodeValidator.IsValid(new string('A', 64), out _));
        Assert.False(PalletCodeValidator.IsValid(new string('A', 65), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Pallet_code_rejects_empty_and_illegal(string? code)
    {
        Assert.False(PalletCodeValidator.IsValid(code, out var error));
        Assert.Equal("托盘码为空", error);
    }

    [Theory]
    [InlineData("***")]
    [InlineData("P 0001")]
    [InlineData("P0001!")]
    [InlineData("托盘一号")]
    [InlineData("P0001#")]
    public void Pallet_code_rejects_illegal_characters(string code)
    {
        Assert.False(PalletCodeValidator.IsValid(code, out var error));
        Assert.Equal("托盘码含非法字符或长度不符", error);
    }

    [Fact]
    public void Pallet_code_trims_whitespace_and_trailing_nul()
    {
        // PLC 字符串读回常带 NUL 填充与前后空白，必须先裁剪再校验。
        Assert.True(PalletCodeValidator.IsValid("  P0001\0\0  ", out _));
        Assert.True(PalletCodeValidator.IsValid("\tP0001\n", out _));
    }

    // ---------- 限值判定 ----------

    [Fact]
    public void Limit_evaluator_flags_out_of_range()
    {
        var tag = new TagDefinition { LowerLimit = 5, UpperLimit = 10, IsRequired = true };
        Assert.True(LimitEvaluator.IsOutOfLimit(tag, 4));
        Assert.False(LimitEvaluator.IsOutOfLimit(tag, 7));
        Assert.Equal(Judgement.Ng, LimitEvaluator.Combine([Judgement.Ok, Judgement.Ng]));
    }

    [Theory]
    [InlineData(5.0, false)]
    [InlineData(10.0, false)]
    [InlineData(4.999, true)]
    [InlineData(10.001, true)]
    public void Limit_evaluator_treats_bounds_as_inclusive(double value, bool outOfLimit)
    {
        var tag = new TagDefinition { LowerLimit = 5, UpperLimit = 10 };
        Assert.Equal(outOfLimit, LimitEvaluator.IsOutOfLimit(tag, value));
    }

    [Fact]
    public void Limit_evaluator_handles_single_sided_and_missing_limits()
    {
        var lowerOnly = new TagDefinition { LowerLimit = 5 };
        Assert.True(LimitEvaluator.IsOutOfLimit(lowerOnly, 4));
        Assert.False(LimitEvaluator.IsOutOfLimit(lowerOnly, 1000));

        var upperOnly = new TagDefinition { UpperLimit = 10 };
        Assert.True(LimitEvaluator.IsOutOfLimit(upperOnly, 11));
        Assert.False(LimitEvaluator.IsOutOfLimit(upperOnly, -1000));

        var noLimit = new TagDefinition();
        Assert.False(LimitEvaluator.IsOutOfLimit(noLimit, double.MaxValue));
        Assert.False(LimitEvaluator.IsOutOfLimit(noLimit, double.MinValue));
    }

    [Fact]
    public void Limit_evaluator_treats_null_value_by_required_flag()
    {
        // 空值无法判限，是否算异常完全取决于该点位是否必填。
        var required = new TagDefinition { IsRequired = true, LowerLimit = 1, UpperLimit = 2 };
        Assert.True(LimitEvaluator.IsOutOfLimit(required, null));

        var optional = new TagDefinition { IsRequired = false, LowerLimit = 1, UpperLimit = 2 };
        Assert.False(LimitEvaluator.IsOutOfLimit(optional, null));
    }

    [Fact]
    public void Limit_evaluator_supports_negative_limits()
    {
        var tag = new TagDefinition { LowerLimit = -40, UpperLimit = -10 };
        Assert.True(LimitEvaluator.IsOutOfLimit(tag, -41));
        Assert.False(LimitEvaluator.IsOutOfLimit(tag, -25));
        Assert.True(LimitEvaluator.IsOutOfLimit(tag, 0));
    }

    // ---------- 三级限值：规格限 / 预警带 / 目标值 ----------

    [Fact]
    public void Limit_evaluator_reports_the_warning_band_between_the_spec_limits()
    {
        var tag = new TagDefinition
        {
            LowerLimit = 5,
            UpperLimit = 20,
            WarningLowerLimit = 7,
            WarningUpperLimit = 18
        };

        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(tag, 4.9));
        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(tag, 6.9));
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(tag, 12));
        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(tag, 18.1));
        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(tag, 20.1));
    }

    [Fact]
    public void Warning_status_never_counts_as_out_of_limit()
    {
        // 这是三级限值的关键约定：黄区只提示，不判废、不改 PLC 响应码。
        var tag = new TagDefinition { LowerLimit = 5, UpperLimit = 20, WarningUpperLimit = 18 };

        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(tag, 19));
        Assert.False(LimitEvaluator.IsOutOfLimit(tag, 19));
        Assert.True(LimitEvaluator.IsOutOfLimit(tag, 21));
    }

    [Fact]
    public void Warning_bounds_are_inclusive_and_work_single_sided()
    {
        var upperOnly = new TagDefinition { LowerLimit = 0, UpperLimit = 10, WarningUpperLimit = 8 };
        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(upperOnly, 8.1));
        // 等于黄线不算预警（与规格限一样按闭区间处理）。
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(upperOnly, 8));
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(upperOnly, 1));

        var lowerOnly = new TagDefinition { WarningLowerLimit = 10 };
        Assert.Equal(LimitStatus.Warning, LimitEvaluator.Evaluate(lowerOnly, 9));
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(lowerOnly, 10));
    }

    [Fact]
    public void Warning_bounds_configured_outside_the_spec_band_are_clamped()
    {
        // 现场把黄线配到了红线外面：按红线收敛，不产生「既不超规格又算预警」的矛盾状态。
        var tag = new TagDefinition
        {
            LowerLimit = 5,
            UpperLimit = 20,
            WarningLowerLimit = 1,
            WarningUpperLimit = 99
        };

        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(tag, 4.9));
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(tag, 5));
        Assert.Equal(LimitStatus.InSpec, LimitEvaluator.Evaluate(tag, 19));
        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(tag, 20.1));
    }

    [Fact]
    public void Target_value_alone_never_produces_a_status()
    {
        // 目标值只用于统计，不参与判定。
        var tag = new TagDefinition { TargetValue = 12 };

        Assert.Equal(LimitStatus.None, LimitEvaluator.Evaluate(tag, 12));
        Assert.False(TagLimits.From(tag).HasAny);
    }

    [Fact]
    public void Has_any_limit_covers_all_four_bounds()
    {
        Assert.False(TagLimits.From(new TagDefinition()).HasAny);
        Assert.True(TagLimits.From(new TagDefinition { LowerLimit = 1 }).HasAny);
        Assert.True(TagLimits.From(new TagDefinition { UpperLimit = 1 }).HasAny);
        Assert.True(TagLimits.From(new TagDefinition { WarningLowerLimit = 1 }).HasAny);
        Assert.True(TagLimits.From(new TagDefinition { WarningUpperLimit = 1 }).HasAny);
    }

    [Fact]
    public void Missing_required_value_still_outranks_the_warning_path()
    {
        var required = new TagDefinition { IsRequired = true, WarningLowerLimit = 10, WarningUpperLimit = 20 };
        Assert.Equal(LimitStatus.OutOfSpec, LimitEvaluator.Evaluate(required, null));

        var optional = new TagDefinition { IsRequired = false, WarningLowerLimit = 10, WarningUpperLimit = 20 };
        Assert.Equal(LimitStatus.None, LimitEvaluator.Evaluate(optional, null));
    }

    // ---------- 判定聚合 ----------

    [Fact]
    public void Combine_returns_none_for_empty_input()
    {
        Assert.Equal(Judgement.None, LimitEvaluator.Combine([]));
    }

    [Fact]
    public void Combine_returns_none_when_all_items_are_none()
    {
        Assert.Equal(Judgement.None, LimitEvaluator.Combine([Judgement.None, Judgement.None]));
    }

    [Fact]
    public void Combine_returns_ok_when_any_item_is_ok_and_none_is_ng()
    {
        Assert.Equal(Judgement.Ok, LimitEvaluator.Combine([Judgement.Ok]));
        Assert.Equal(Judgement.Ok, LimitEvaluator.Combine([Judgement.None, Judgement.Ok, Judgement.None]));
    }

    [Fact]
    public void Combine_lets_ng_dominate_everything()
    {
        Assert.Equal(Judgement.Ng, LimitEvaluator.Combine([Judgement.Ok, Judgement.Ng, Judgement.None]));
        Assert.Equal(Judgement.Ng, LimitEvaluator.Combine([Judgement.Ng]));
    }

    // ---------- 握手响应码 ----------

    [Theory]
    [InlineData(ResultCodes.Trigger, "触发待采集")]
    [InlineData(ResultCodes.Success, "采集成功")]
    [InlineData(ResultCodes.PlcReadFailed, "PLC 读取失败")]
    [InlineData(ResultCodes.InvalidPalletCode, "托盘码非法")]
    [InlineData(ResultCodes.DataValidationFailed, "数据校验失败")]
    [InlineData(ResultCodes.DatabaseWriteFailed, "数据库写入失败")]
    [InlineData(ResultCodes.InternalError, "系统内部异常")]
    [InlineData(ResultCodes.ProcessAbnormal, "流程异常（跳站等）")]
    public void Result_codes_describe_known_codes(short code, string expected)
        => Assert.Equal(expected, ResultCodes.Describe(code));

    [Fact]
    public void Result_codes_describe_unknown_code()
    {
        Assert.Equal("未知码 99", ResultCodes.Describe(99));
        Assert.Equal("未知码 0", ResultCodes.Describe(0));
    }

    [Fact]
    public void Result_codes_are_contiguous_and_distinct()
    {
        // 1 为触发，2–8 为采集结果，必须连续且互不相同，PLC 侧按同一套码表解读。
        short[] codes =
        [
            ResultCodes.Trigger, ResultCodes.Success, ResultCodes.PlcReadFailed,
            ResultCodes.InvalidPalletCode, ResultCodes.DataValidationFailed,
            ResultCodes.DatabaseWriteFailed, ResultCodes.InternalError, ResultCodes.ProcessAbnormal
        ];

        Assert.Equal(codes.Distinct().Count(), codes.Length);
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (short)i), codes);
    }

    // ---------- 角色 ----------

    [Fact]
    public void App_roles_are_unique_and_complete()
    {
        Assert.Equal(4, AppRoles.All.Length);
        Assert.Equal(AppRoles.All.Length, AppRoles.All.Distinct().Count());
        Assert.Contains(AppRoles.Administrator, AppRoles.All);
        Assert.Contains(AppRoles.Engineer, AppRoles.All);
        Assert.Contains(AppRoles.Operator, AppRoles.All);
        Assert.Contains(AppRoles.Viewer, AppRoles.All);
    }

    // ---------- 枚举契约 ----------

    [Fact]
    public void Enum_underlying_values_are_stable_for_plc_and_database()
    {
        // 这些数值直接落库并跨版本读取，改动即破坏兼容，故用测试锁死。
        Assert.Equal(0, (int)PlcBrand.Simulator);
        Assert.Equal(4, (int)PlcBrand.OmronFins);
        Assert.Equal(0, (int)Judgement.None);
        Assert.Equal(2, (int)Judgement.Ng);
        Assert.Equal(0, (int)SessionStatus.Open);
        Assert.Equal(2, (int)SessionStatus.Abnormal);
        Assert.Equal(0, (int)SeriesRole.X);
        Assert.Equal(1, (int)SeriesRole.Y);
        Assert.Equal(0, (int)FloatWordOrder.ABCD);
        Assert.Equal(3, (int)FloatWordOrder.DCBA);
        Assert.Equal(0, (int)LimitStatus.None);
        Assert.Equal(3, (int)LimitStatus.OutOfSpec);
    }
}
