using DataTrace.Domain.Evaluation;

namespace DataTrace.Tests;

/// <summary>
/// 判异准则（Nelson 规则 1–5）。
/// 这里手工构造控制图统计量，把规则逻辑与统计量计算彻底隔离：
/// 规则测试只关心"给定中心线与 σ，这串数据该不该报警"。
/// 相邻/重叠的命中窗口必须合并成一段，否则一次持续漂移会刷出十几条重复告警。
/// </summary>
public class SpcRuleTests
{
    /// <summary>构造一张中心线 0、σ 给定的控制图（控制限=±3σ）。</summary>
    private static SpcSummary Chart(double sigma = 1) => new()
    {
        Count = 0,
        Mean = 0,
        CenterLine = 0,
        WithinStdDev = sigma,
        UpperControlLimit = 3 * sigma,
        LowerControlLimit = -3 * sigma
    };

    private static IReadOnlyList<SpcViolation> Of(IReadOnlyList<double> values, SpcRule rule, double sigma = 1)
        => SpcRuleEvaluator.Evaluate(values, Chart(sigma)).Where(v => v.Rule == rule).ToList();

    private static double[] Series(params double[] values) => values;

    // ---------- 规则 1：超出控制限 ----------

    [Fact]
    public void Rule1_flags_points_beyond_the_control_limits()
    {
        var violations = Of(Series(0, 0, 0, 0, 3.5, 0, 0, -4, 0), SpcRule.BeyondControlLimit);

        Assert.Equal(2, violations.Count);
        Assert.Equal(4, violations[0].StartIndex);
        Assert.Equal(4, violations[0].EndIndex);
        Assert.Equal(1, violations[0].Count);
        Assert.Equal(7, violations[1].StartIndex);
        Assert.Equal(7, violations[1].EndIndex);
        Assert.Equal(1, violations[1].Count);
    }

    [Fact]
    public void Rule1_merges_consecutive_out_of_limit_points_into_one_alarm()
    {
        var violations = Of(Series(0, 3.5, 3.6, 0), SpcRule.BeyondControlLimit);

        var violation = Assert.Single(violations);
        Assert.Equal(1, violation.StartIndex);
        Assert.Equal(2, violation.EndIndex);
        Assert.Equal(2, violation.Count);
        Assert.Contains("连续 2 点", violation.Description);
    }

    [Fact]
    public void Rule1_treats_values_exactly_on_the_limit_as_within()
    {
        Assert.Empty(Of(Series(0, 3.0, 0, -3.0, 0), SpcRule.BeyondControlLimit));
    }

    // ---------- 规则 2：连续 9 点同侧 ----------

    [Fact]
    public void Rule2_flags_nine_consecutive_points_on_one_side()
    {
        var violations = Of(Series(1, 1, 1, 1, 1, 1, 1, 1, 1), SpcRule.NineOnOneSide);

        var violation = Assert.Single(violations);
        Assert.Equal(0, violation.StartIndex);
        Assert.Equal(8, violation.EndIndex);
        Assert.Contains("中心线同侧连续 9 点", violation.Description);
    }

    [Fact]
    public void Rule2_merges_a_longer_run_into_a_single_alarm()
    {
        var violations = Of(
            Series(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1),
            SpcRule.NineOnOneSide);

        var violation = Assert.Single(violations);
        Assert.Equal(0, violation.StartIndex);
        Assert.Equal(11, violation.EndIndex);
        Assert.Contains("连续 12 点", violation.Description);
    }

    [Fact]
    public void Rule2_does_not_fire_for_eight_points_or_when_a_breaker_sits_inside()
    {
        Assert.Empty(Of(Series(1, 1, 1, 1, 1, 1, 1, 1), SpcRule.NineOnOneSide));
        // 第九个点翻到另一侧，任何 9 点窗口都不成立。
        Assert.Empty(Of(Series(1, 1, 1, 1, 1, 1, 1, 1, -1), SpcRule.NineOnOneSide));
        // 等于中心线的点同样打断连续。
        Assert.Empty(Of(Series(1, 1, 1, 1, 1, 1, 1, 1, 0), SpcRule.NineOnOneSide));
    }

    // ---------- 规则 3：连续 6 点单调 ----------

    [Fact]
    public void Rule3_flags_six_monotonic_points_in_either_direction()
    {
        var rising = Of(Series(1, 2, 3, 4, 5, 6), SpcRule.SixMonotonic);
        Assert.Single(rising);
        Assert.Equal(0, rising[0].StartIndex);
        Assert.Equal(5, rising[0].EndIndex);

        var falling = Of(Series(6, 5, 4, 3, 2, 1), SpcRule.SixMonotonic);
        Assert.Single(falling);
        Assert.Contains("连续 6 点", falling[0].Description);
    }

    [Fact]
    public void Rule3_merges_an_extended_trend()
    {
        var violations = Of(Series(1, 2, 3, 4, 5, 6, 7), SpcRule.SixMonotonic);

        var violation = Assert.Single(violations);
        Assert.Equal(0, violation.StartIndex);
        Assert.Equal(6, violation.EndIndex);
        Assert.Contains("连续 7 点", violation.Description);
    }

    [Fact]
    public void Rule3_requires_strict_monotonicity()
    {
        Assert.Empty(Of(Series(1, 2, 3, 4, 5, 5), SpcRule.SixMonotonic));
        Assert.Empty(Of(Series(1, 2, 3, 4, 5), SpcRule.SixMonotonic));
    }

    // ---------- 规则 4：连续 14 点上下交替 ----------

    [Fact]
    public void Rule4_flags_fourteen_alternating_points()
    {
        var values = new double[14];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i % 2 == 0 ? 1 : -1;
        }

        var violations = Of(values, SpcRule.FourteenAlternating);

        var violation = Assert.Single(violations);
        Assert.Equal(0, violation.StartIndex);
        Assert.Equal(13, violation.EndIndex);
        Assert.Contains("上下交替", violation.Description);
    }

    [Fact]
    public void Rule4_does_not_fire_below_the_threshold_or_when_a_repeat_breaks_the_pattern()
    {
        var thirteen = new double[13];
        for (var i = 0; i < thirteen.Length; i++)
        {
            thirteen[i] = i % 2 == 0 ? 1 : -1;
        }

        Assert.Empty(Of(thirteen, SpcRule.FourteenAlternating));

        // 中间插一个与前值相等的点，方向算不出符号，交替中断。
        var broken = new double[14];
        for (var i = 0; i < broken.Length; i++)
        {
            broken[i] = i % 2 == 0 ? 1 : -1;
        }

        broken[7] = broken[6];
        Assert.Empty(Of(broken, SpcRule.FourteenAlternating));
    }

    // ---------- 规则 5：连续 3 点中 2 点同侧超 ±2σ ----------

    [Fact]
    public void Rule5_flags_two_of_three_beyond_two_sigma_on_the_same_side()
    {
        var violations = Of(Series(2.5, 2.6, 0, 0, 0), SpcRule.TwoOfThreeBeyondTwoSigma);

        var violation = Assert.Single(violations);
        Assert.Equal(0, violation.StartIndex);
        Assert.Equal(2, violation.EndIndex);

        var below = Of(Series(-2.5, -2.6, 0, 0, 0), SpcRule.TwoOfThreeBeyondTwoSigma);
        Assert.Single(below);
    }

    [Fact]
    public void Rule5_merges_overlapping_windows()
    {
        // 窗口 [0,2] 与 [1,3] 都命中，应合并成 [0,3]。
        var violations = Of(Series(2.5, 2.6, 2.7, 0, 0), SpcRule.TwoOfThreeBeyondTwoSigma);

        var violation = Assert.Single(violations);
        Assert.Equal(0, violation.StartIndex);
        Assert.Equal(3, violation.EndIndex);
        Assert.Contains("连续 4 点", violation.Description);
    }

    [Fact]
    public void Rule5_ignores_isolated_excursions()
    {
        // 两次越界之间隔了正常点，没有任何 3 点窗口能凑够 2 个同侧越界。
        Assert.Empty(Of(Series(2.5, 0, 0, 0, 2.6, 0, 0), SpcRule.TwoOfThreeBeyondTwoSigma));
        // 异侧越界不能互相凑数。
        Assert.Empty(Of(Series(2.5, 0, 0, 0, -2.6, 0, 0), SpcRule.TwoOfThreeBeyondTwoSigma));
    }

    // ---------- 整体行为 ----------

    [Fact]
    public void Empty_input_produces_no_violations()
    {
        Assert.Empty(SpcRuleEvaluator.Evaluate([], Chart()));
    }

    [Fact]
    public void A_well_behaved_process_stays_silent()
    {
        // 成对台阶（10.0,10.0,10.1,10.1, …）：等值点打断单调与交替，
        // 同侧段只有 2 个点，波动远小于 2σ，因此五条规则都不该命中。
        var values = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            values.Add(10.0);
            values.Add(10.0);
            values.Add(10.1);
            values.Add(10.1);
        }

        var summary = SpcCalculator.Compute(values);
        Assert.Equal(0.04871794871794872, summary.MovingRangeMean, 1e-12);
        Assert.Empty(SpcRuleEvaluator.Evaluate(values, summary));
    }

    [Fact]
    public void A_constant_process_stays_silent()
    {
        // MR̄ = 0 时控制限退化成中心线一点，任何规则都不该命中。
        var values = Enumerable.Repeat(10d, 40).ToList();
        var summary = SpcCalculator.Compute(values);

        Assert.Empty(SpcRuleEvaluator.Evaluate(values, summary));
    }

    [Fact]
    public void Control_chart_catches_a_mean_shift_that_capability_indices_miss()
    {
        // 前半段稳定在 10.1、后半段整体上移到 10.7。没有任何单点越出 3σ，
        // 但过程已经明显跑偏 —— 这正是控制图不能省的理由：
        // 规格限 5~15 下 Cpk 依然很高，光看能力指数完全发现不了。
        var values = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            values.Add(i % 2 == 0 ? 10.0 : 10.2);
        }

        for (var i = 0; i < 20; i++)
        {
            values.Add(i % 2 == 0 ? 10.6 : 10.8);
        }

        var summary = SpcCalculator.Compute(values, lowerSpec: 5, upperSpec: 15);
        var violations = SpcRuleEvaluator.Evaluate(values, summary);

        Assert.Equal(10.4, summary.Mean, 1e-9);

        // 单点都在控制限内：规则 1 不该命中。
        Assert.DoesNotContain(violations, v => v.Rule == SpcRule.BeyondControlLimit);
        Assert.All(values, v => Assert.InRange(v, summary.LowerControlLimit, summary.UpperControlLimit));

        // 规则 2 应当给出两段：前 20 点同侧偏低，后 20 点同侧偏高。
        var sameSide = violations.Where(v => v.Rule == SpcRule.NineOnOneSide).ToList();
        Assert.Equal(2, sameSide.Count);
        Assert.Equal(0, sameSide[0].StartIndex);
        Assert.Equal(19, sameSide[0].EndIndex);
        Assert.Equal(20, sameSide[1].StartIndex);
        Assert.Equal(39, sameSide[1].EndIndex);

        // 而 Cpk 因为规格带宽裕，看起来毫无问题。
        Assert.Equal(SpcVerdict.Capable, summary.Verdict);
        Assert.True(summary.Cpk > 1.33);
    }
}
