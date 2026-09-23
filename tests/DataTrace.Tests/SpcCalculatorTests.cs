using DataTrace.Domain.Evaluation;

namespace DataTrace.Tests;

/// <summary>
/// I-MR 控制图与过程能力指数：公式必须与手算一致。
/// 所有能力指数在分母无效时必须是 null，绝不能出现 NaN / Infinity —— 界面会直接显示"无法评估"。
/// </summary>
public class SpcCalculatorTests
{
    private const double Tolerance = 1e-9;

    private static double D2 => 1.128;

    private static double E2 => 3.0 / D2;

    /// <summary>交替两点，移动极差恒定，便于手算 σ 与能力指数。</summary>
    private static double[] Alternating(double low, double high, int pairs)
    {
        var values = new double[pairs * 2];
        for (var i = 0; i < pairs; i++)
        {
            values[i * 2] = low;
            values[i * 2 + 1] = high;
        }

        return values;
    }

    // ---------- 控制图统计量 ----------

    [Fact]
    public void Control_limits_follow_the_imr_formulas()
    {
        var values = new double[] { 10, 12, 11, 13, 12 };

        var summary = SpcCalculator.Compute(values);

        Assert.Equal(5, summary.Count);
        Assert.Equal(11.6, summary.Mean, Tolerance);
        Assert.Equal(11.6, summary.CenterLine, Tolerance);
        Assert.Equal(10d, summary.Min);
        Assert.Equal(13d, summary.Max);

        // 移动极差：2、1、2、1 → 均值 1.5
        Assert.Equal(1.5, summary.MovingRangeMean, Tolerance);
        Assert.Equal(1.5 / D2, summary.WithinStdDev, Tolerance);
        Assert.Equal(11.6 + E2 * 1.5, summary.UpperControlLimit, Tolerance);
        Assert.Equal(11.6 - E2 * 1.5, summary.LowerControlLimit, Tolerance);
        Assert.Equal(3.267 * 1.5, summary.MovingRangeUpperLimit, Tolerance);

        // 总体标准差用 n-1：偏差平方和 5.2 / 4 = 1.3
        Assert.Equal(Math.Sqrt(1.3), summary.OverallStdDev, Tolerance);

        Assert.True(summary.UpperControlLimit > summary.CenterLine);
        Assert.True(summary.LowerControlLimit < summary.CenterLine);
    }

    [Fact]
    public void Within_std_dev_uses_moving_range_while_overall_uses_sample_std_dev()
    {
        // 交替序列的组内波动由 MR 决定，与总体标准差不是同一个量 —— 两者不能混用。
        var summary = SpcCalculator.Compute(Alternating(9, 11, 10));

        Assert.Equal(2, summary.MovingRangeMean, Tolerance);
        Assert.Equal(2 / D2, summary.WithinStdDev, Tolerance);
        // 20 个点各偏离均值 1：平方和 20 / (20-1)
        Assert.Equal(Math.Sqrt(20d / 19d), summary.OverallStdDev, Tolerance);
        Assert.NotEqual(summary.WithinStdDev, summary.OverallStdDev, 1e-6);
    }

    // ---------- 能力指数 ----------

    [Fact]
    public void Capability_indices_match_hand_calculation()
    {
        // 40 点交替 9/11：均值 10，MR̄ = 2，σ_within = 2/1.128
        var summary = SpcCalculator.Compute(Alternating(9, 11, 20), lowerSpec: 5, upperSpec: 15);

        var within = 2 / D2;
        // 40 个点各偏离均值 1：平方和 40 / (40-1)
        var overall = Math.Sqrt(40d / 39d);

        Assert.Equal(10d / (6 * within), summary.Cp!.Value, 1e-9);
        Assert.Equal(5d / (3 * within), summary.Cpu!.Value, 1e-9);
        Assert.Equal(5d / (3 * within), summary.Cpl!.Value, 1e-9);
        Assert.Equal(summary.Cpu, summary.Cpk);
        Assert.Equal(10d / (6 * overall), summary.Pp!.Value, 1e-9);
        Assert.Equal(5d / (3 * overall), summary.Ppk!.Value, 1e-9);

        // Cpk 用组内 σ、Ppk 用总体 σ，两者的差反映过程漂移；此处 Cpk 必然小于 Ppk。
        Assert.True(summary.Cpk < summary.Ppk);
        Assert.True(summary.HasSpecLimits);
    }

    [Fact]
    public void Single_sided_spec_yields_one_sided_capability()
    {
        var values = Alternating(9, 11, 20);

        var upperOnly = SpcCalculator.Compute(values, upperSpec: 15);
        Assert.Null(upperOnly.Cp);
        Assert.Null(upperOnly.Cpl);
        Assert.NotNull(upperOnly.Cpu);
        Assert.Equal(upperOnly.Cpu, upperOnly.Cpk);

        var lowerOnly = SpcCalculator.Compute(values, lowerSpec: 5);
        Assert.Null(lowerOnly.Cp);
        Assert.Null(lowerOnly.Cpu);
        Assert.NotNull(lowerOnly.Cpl);
        Assert.Equal(lowerOnly.Cpl, lowerOnly.Cpk);
    }

    [Fact]
    public void Cpk_takes_the_worse_side_when_centered_off_target()
    {
        // 均值 10，规格 5~17：上侧余量 7、下侧余量 5，应取下侧。
        var summary = SpcCalculator.Compute(Alternating(9, 11, 20), lowerSpec: 5, upperSpec: 17);

        Assert.Equal(5d / (3 * (2 / D2)), summary.Cpk!.Value, 1e-9);
        Assert.Equal(summary.Cpl, summary.Cpk);
        Assert.True(summary.Cpu > summary.Cpl);
    }

    // ---------- 结论 ----------

    [Fact]
    public void Sample_shortage_reports_the_number_but_refuses_to_conclude()
    {
        // 10 个样本算出来的 Cpk 会过度乐观，必须只给数字不下结论。
        var summary = SpcCalculator.Compute(Alternating(9, 11, 5), lowerSpec: 5, upperSpec: 15);

        Assert.Equal(10, summary.Count);
        Assert.NotNull(summary.Cpk);
        Assert.Equal(SpcVerdict.InsufficientData, summary.Verdict);
        Assert.Contains("样本不足", summary.Note);
        Assert.Contains("10", summary.Note);
    }

    [Theory]
    [InlineData(9.0, 11.0, SpcVerdict.Incapable)]
    [InlineData(9.2, 10.8, SpcVerdict.Marginal)]
    [InlineData(9.5, 10.5, SpcVerdict.Capable)]
    public void Verdict_follows_the_cpk_thresholds(double low, double high, SpcVerdict expected)
    {
        var summary = SpcCalculator.Compute(Alternating(low, high, 20), lowerSpec: 5, upperSpec: 15);

        Assert.Equal(40, summary.Count);
        Assert.Equal(expected, summary.Verdict);
        Assert.Null(summary.Note);
    }

    [Fact]
    public void Missing_spec_limits_prevents_any_capability_claim()
    {
        var summary = SpcCalculator.Compute(Alternating(9, 11, 20));

        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpk);
        Assert.Null(summary.Pp);
        Assert.Null(summary.Ppk);
        Assert.False(summary.HasSpecLimits);
        Assert.Equal(SpcVerdict.InsufficientData, summary.Verdict);
        Assert.Contains("规格限", summary.Note);
        // 没有规格限也照样给控制图，控制限与被判废与否无关。
        Assert.True(summary.UpperControlLimit > summary.CenterLine);
    }

    // ---------- 退化输入 ----------

    [Fact]
    public void Empty_sample_returns_a_usable_summary_without_throwing()
    {
        var summary = SpcCalculator.Compute([], lowerSpec: 5, upperSpec: 15);

        Assert.Equal(0, summary.Count);
        Assert.Equal(0d, summary.Mean);
        Assert.Null(summary.Cpk);
        Assert.Equal(SpcVerdict.InsufficientData, summary.Verdict);
        Assert.Contains("没有采样数据", summary.Note);
    }

    [Fact]
    public void Single_sample_has_no_spread()
    {
        var summary = SpcCalculator.Compute([7.5], lowerSpec: 5, upperSpec: 15);

        Assert.Equal(1, summary.Count);
        Assert.Equal(7.5, summary.Mean, Tolerance);
        Assert.Equal(0d, summary.MovingRangeMean);
        Assert.Equal(0d, summary.WithinStdDev);
        Assert.Equal(0d, summary.OverallStdDev);
        Assert.Equal(7.5, summary.UpperControlLimit, Tolerance);
        Assert.Equal(7.5, summary.LowerControlLimit, Tolerance);
        Assert.Null(summary.Cpk);
    }

    [Fact]
    public void Zero_variation_never_produces_nan_or_infinity()
    {
        // 读了常量寄存器（或点位根本没刷新）时会出现全等值：不能给出 NaN / Infinity 的能力指数。
        var values = Enumerable.Repeat(10d, 40).ToArray();

        var summary = SpcCalculator.Compute(values, lowerSpec: 5, upperSpec: 15);

        Assert.Equal(0d, summary.MovingRangeMean);
        Assert.Equal(0d, summary.WithinStdDev);
        Assert.Equal(0d, summary.OverallStdDev);
        Assert.Null(summary.Cp);
        Assert.Null(summary.Cpk);
        Assert.Null(summary.Pp);
        Assert.Null(summary.Ppk);
        Assert.Null(summary.MeanOffsetInSigma);
        Assert.Equal(10d, summary.CenterLine, Tolerance);
        Assert.Equal(10d, summary.UpperControlLimit, Tolerance);
        Assert.Equal(10d, summary.LowerControlLimit, Tolerance);
        Assert.Equal(SpcVerdict.InsufficientData, summary.Verdict);
        Assert.Contains("波动为 0", summary.Note);

        Assert.True(double.IsFinite(summary.UpperControlLimit));
        Assert.True(double.IsFinite(summary.LowerControlLimit));
        Assert.True(double.IsFinite(summary.WithinStdDev));
    }

    // ---------- 均值偏移 ----------

    [Fact]
    public void Mean_offset_is_measured_in_within_sigma()
    {
        var values = Alternating(9, 11, 20);
        var sigma = 2 / D2;

        // 未给目标值时以规格中心为基准：均值 10 正好在 5~15 中心。
        var centered = SpcCalculator.Compute(values, lowerSpec: 5, upperSpec: 15);
        Assert.Equal(0d, centered.MeanOffsetInSigma!.Value, Tolerance);

        // 给了目标值 12 则以目标值为基准：(10-12)/σ。
        var shifted = SpcCalculator.Compute(values, lowerSpec: 5, upperSpec: 15, targetValue: 12);
        Assert.Equal(-2 / sigma, shifted.MeanOffsetInSigma!.Value, 1e-9);
    }

    [Fact]
    public void Missing_value_is_formatted_as_a_dash()
    {
        Assert.Equal("-", SpcCalculator.Format(null));
        Assert.Equal("1.33", SpcCalculator.Format(1.3333));
        Assert.Equal("0.94", SpcCalculator.Format(0.94, "0.00"));
        Assert.Equal("1.334", SpcCalculator.Format(1.3336, "0.000"));
    }
}
