using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Tests;

/// <summary>
/// 曲线特征提取与曲线判据求值：全部为纯函数，边界行为必须确定，
/// 因为采集主链路的判定结果直接建立在它们之上。
/// </summary>
public class CurveFeatureTests
{
    private const double Tolerance = 1e-9;

    // ---------- 特征提取 ----------

    [Fact]
    public void Empty_series_yields_zero_features_without_throwing()
    {
        var features = CurveFeatureExtractor.Extract([]);

        Assert.Equal(0, features.PointCount);
        Assert.Equal(0d, features.Min);
        Assert.Equal(0d, features.Peak);
        Assert.Equal(0d, features.Mean);
        Assert.Equal(0d, features.StdDev);
        Assert.Equal(0d, features.Area);
        Assert.Equal(0d, features.RiseSlope);
        Assert.Equal(0d, features.HoldSlope);
        Assert.Equal(0d, features.FallRatio);
        Assert.Equal(0d, features.MaxStep);
        Assert.Equal(0, features.Oscillations);
    }

    [Fact]
    public void Single_point_has_no_slope_and_no_area()
    {
        var features = CurveFeatureExtractor.Extract([4.5f]);

        Assert.Equal(1, features.PointCount);
        Assert.Equal(4.5d, features.Min);
        Assert.Equal(4.5d, features.Peak);
        Assert.Equal(4.5d, features.Mean);
        Assert.Equal(0d, features.StdDev);
        Assert.Equal(0d, features.Area);
        Assert.Equal(0d, features.RiseSlope);
        Assert.Equal(0d, features.HoldSlope);
        Assert.Equal(0, features.RiseSpan);
        Assert.Equal(0d, features.FallRatio);
    }

    [Fact]
    public void Constant_series_has_zero_range_derived_features()
    {
        // 常值序列的量程为 0，所有比值/斜率类特征必须退化为 0 而不是 NaN。
        var features = CurveFeatureExtractor.Extract([5f, 5f, 5f, 5f]);

        Assert.Equal(5d, features.Min);
        Assert.Equal(5d, features.Peak);
        Assert.Equal(5d, features.Mean);
        Assert.Equal(0d, features.StdDev);
        Assert.Equal(15d, features.Area, Tolerance);
        Assert.Equal(0d, features.RiseSlope);
        Assert.Equal(0d, features.HoldSlope);
        Assert.Equal(0d, features.FallRatio);
        Assert.Equal(0d, features.MaxStep);
        Assert.Equal(0, features.Oscillations);
    }

    [Fact]
    public void Ramp_produces_unit_slope_and_full_area()
    {
        var features = CurveFeatureExtractor.Extract([0f, 1f, 2f, 3f, 4f]);

        Assert.Equal(0d, features.Min);
        Assert.Equal(0, features.MinIndex);
        Assert.Equal(4d, features.Peak);
        Assert.Equal(4, features.PeakIndex);
        Assert.Equal(2d, features.Mean);
        Assert.Equal(Math.Sqrt(2), features.StdDev, 1e-6);
        // 梯形法：(0+1)/2 + (1+2)/2 + (2+3)/2 + (3+4)/2 = 8
        Assert.Equal(8d, features.Area, Tolerance);
        Assert.Equal(1d, features.RiseSlope, 1e-6);
        // 峰值即末点，保压段只有一个点，斜率退化为 0。
        Assert.Equal(0d, features.HoldSlope);
        Assert.Equal(1d, features.MaxStep, Tolerance);
        // 起升点 = 首个 >= min + 10% 量程 = 0.4 的采样点 → 序号 1。
        Assert.Equal(1, features.RiseIndex);
        Assert.Equal(3, features.RiseSpan);
        Assert.Equal(0d, features.FallRatio, Tolerance);
    }

    [Fact]
    public void Hump_has_positive_rise_and_negative_hold_slope()
    {
        var features = CurveFeatureExtractor.Extract([0f, 1f, 2f, 3f, 2f, 1f, 0f]);

        Assert.Equal(0d, features.Min);
        Assert.Equal(3d, features.Peak);
        Assert.Equal(3, features.PeakIndex);
        Assert.Equal(9d / 7d, features.Mean, 1e-6);
        Assert.Equal(9d, features.Area, Tolerance);
        Assert.Equal(1d, features.RiseSlope, 1e-6);
        Assert.Equal(-1d, features.HoldSlope, 1e-6);
        Assert.Equal(1, features.RiseIndex);
        Assert.Equal(2, features.RiseSpan);
        // 末值回到了最小值，回落比例应为满量程。
        Assert.Equal(1d, features.FallRatio, Tolerance);
    }

    [Fact]
    public void Max_step_captures_the_largest_jump()
    {
        var features = CurveFeatureExtractor.Extract([0f, 0f, 5f, 0f]);

        Assert.Equal(5d, features.MaxStep, Tolerance);
    }

    [Fact]
    public void Oscillations_count_mean_line_crossings()
    {
        var alternating = CurveFeatureExtractor.Extract([-1f, 1f, -1f, 1f]);
        Assert.Equal(3, alternating.Oscillations);

        var constant = CurveFeatureExtractor.Extract([2f, 2f, 2f, 2f]);
        Assert.Equal(0, constant.Oscillations);
    }

    [Fact]
    public void Selecting_extreme_indices_prefers_the_first_occurrence()
    {
        var features = CurveFeatureExtractor.Extract([1f, 9f, 1f, 9f]);

        // 峰值取首次出现，最小值同理，保证同一波形多次提取结果稳定。
        Assert.Equal(1, features.PeakIndex);
        Assert.Equal(0, features.MinIndex);
    }

    [Fact]
    public void To_entity_mirrors_every_feature_field()
    {
        var set = CurveFeatureExtractor.Extract([0f, 1f, 2f]);
        var entity = CurveFeatureExtractor.ToEntity("压力", SeriesRole.Y, set);

        Assert.Equal("压力", entity.SeriesName);
        Assert.Equal(SeriesRole.Y, entity.Role);
        Assert.Equal(set.PointCount, entity.PointCount);
        Assert.Equal(set.Min, entity.Min);
        Assert.Equal(set.MinIndex, entity.MinIndex);
        Assert.Equal(set.Peak, entity.Peak);
        Assert.Equal(set.PeakIndex, entity.PeakIndex);
        Assert.Equal(set.Mean, entity.Mean);
        Assert.Equal(set.StdDev, entity.StdDev);
        Assert.Equal(set.Area, entity.Area);
        Assert.Equal(set.RiseSlope, entity.RiseSlope);
        Assert.Equal(set.HoldSlope, entity.HoldSlope);
        Assert.Equal(set.RiseIndex, entity.RiseIndex);
        Assert.Equal(set.RiseSpan, entity.RiseSpan);
        Assert.Equal(set.FallRatio, entity.FallRatio);
        Assert.Equal(set.MaxStep, entity.MaxStep);
        Assert.Equal(set.Oscillations, entity.Oscillations);
    }

    // ---------- 判据求值 ----------

    [Fact]
    public void Empty_criterion_never_reports_a_violation()
    {
        var features = CurveFeatureExtractor.Extract([0f, 1f, 2f]);

        Assert.Empty(CurveCriterionEvaluator.Evaluate(new CurveCriterion(), features));
    }

    [Fact]
    public void Peak_bounds_report_lower_and_upper_violations()
    {
        var features = CurveFeatureExtractor.Extract([0f, 1f, 2f]);

        var tooHigh = CurveCriterionEvaluator.Evaluate(new CurveCriterion { PeakMax = 1.5 }, features);
        Assert.Single(tooHigh);
        Assert.Contains("峰值", tooHigh[0]);
        Assert.Contains("超过上限", tooHigh[0]);

        var tooLow = CurveCriterionEvaluator.Evaluate(new CurveCriterion { PeakMin = 5 }, features);
        Assert.Single(tooLow);
        Assert.Contains("峰值", tooLow[0]);
        Assert.Contains("低于下限", tooLow[0]);
    }

    [Fact]
    public void Curve_criteria_cover_slope_area_and_shape_limits()
    {
        // 驼峰波形：峰值 3、均值 1.2857、面积 9、上升斜率 1、保压斜率 -1、
        // 回落比例 1、单步跳变 1、标准差 ≈1.03、振荡 2。
        var features = CurveFeatureExtractor.Extract([0f, 1f, 2f, 3f, 2f, 1f, 0f]);

        var reasons = CurveCriterionEvaluator.Evaluate(
            new CurveCriterion
            {
                PeakMin = 3.5,
                MeanMax = 1.0,
                AreaMax = 5,
                RiseSlopeMin = 2,
                // 保压段斜率 -1 掉得比 -0.5 更陡 → 触发下限。
                HoldSlopeMin = -0.5,
                FallRatioMax = 0.5,
                MaxStepMax = 0.5,
                StdDevMax = 0.5,
                OscillationMax = 1
            },
            features);

        // 九个条件全部不满足，逐条都要能被指出。
        Assert.Equal(9, reasons.Count);
        Assert.Contains(reasons, r => r.Contains("峰值"));
        Assert.Contains(reasons, r => r.Contains("均值"));
        Assert.Contains(reasons, r => r.Contains("面积"));
        Assert.Contains(reasons, r => r.Contains("上升段斜率"));
        Assert.Contains(reasons, r => r.Contains("保压段斜率"));
        Assert.Contains(reasons, r => r.Contains("回落比例"));
        Assert.Contains(reasons, r => r.Contains("单步跳变"));
        Assert.Contains(reasons, r => r.Contains("波动标准差"));
        Assert.Contains(reasons, r => r.Contains("振荡次数"));
    }

    [Fact]
    public void In_range_curve_passes_every_criterion()
    {
        var features = CurveFeatureExtractor.Extract([0f, 1f, 2f, 3f, 2f, 1f, 0f]);

        var reasons = CurveCriterionEvaluator.Evaluate(
            new CurveCriterion
            {
                PeakMin = 0,
                PeakMax = 10,
                RiseSlopeMin = 0.5,
                RiseSlopeMax = 1.5,
                HoldSlopeMin = -2,
                MaxStepMax = 2
            },
            features);

        Assert.Empty(reasons);
    }

    // ---------- 判据与序列的匹配 ----------

    private static CurveDefinition CurveWithTwoSeries() => new()
    {
        Id = 7,
        Series =
        [
            new CurveSeries { Id = 71, Name = "压力", Role = SeriesRole.Y },
            new CurveSeries { Id = 72, Name = "位移", Role = SeriesRole.X }
        ]
    };

    [Fact]
    public void Primary_series_prefers_y_role()
    {
        var curve = CurveWithTwoSeries();

        Assert.Equal(71, CurveCriterionEvaluator.PrimarySeries(curve)!.Id);
    }

    [Fact]
    public void Primary_series_falls_back_to_first_when_no_y_role()
    {
        var curve = new CurveDefinition
        {
            Series = [new CurveSeries { Id = 5, Name = "位移", Role = SeriesRole.X }]
        };

        Assert.Equal(5, CurveCriterionEvaluator.PrimarySeries(curve)!.Id);
        Assert.Null(CurveCriterionEvaluator.PrimarySeries(new CurveDefinition()));
    }

    [Fact]
    public void Empty_series_name_targets_the_primary_series_only()
    {
        var curve = CurveWithTwoSeries();
        var primary = CurveCriterionEvaluator.PrimarySeries(curve);
        var criterion = new CurveCriterion();

        Assert.True(CurveCriterionEvaluator.AppliesTo(criterion, curve.Series.First(s => s.Id == 71), primary));
        Assert.False(CurveCriterionEvaluator.AppliesTo(criterion, curve.Series.First(s => s.Id == 72), primary));
    }

    [Fact]
    public void Explicit_series_name_matches_ignoring_case_and_padding()
    {
        var curve = CurveWithTwoSeries();
        var primary = CurveCriterionEvaluator.PrimarySeries(curve);
        var criterion = new CurveCriterion { SeriesName = "  位移 " };

        Assert.True(CurveCriterionEvaluator.AppliesTo(criterion, curve.Series.First(s => s.Id == 72), primary));
        Assert.False(CurveCriterionEvaluator.AppliesTo(criterion, curve.Series.First(s => s.Id == 71), primary));

        var unknown = new CurveCriterion { SeriesName = "速度" };
        Assert.False(CurveCriterionEvaluator.AppliesTo(unknown, curve.Series.First(s => s.Id == 71), primary));
    }

    [Fact]
    public void Disabled_criterion_never_applies()
    {
        var curve = CurveWithTwoSeries();
        var primary = CurveCriterionEvaluator.PrimarySeries(curve);
        var criterion = new CurveCriterion { Enabled = false, SeriesName = "压力" };

        Assert.False(CurveCriterionEvaluator.AppliesTo(criterion, curve.Series.First(s => s.Id == 71), primary));
    }
}
