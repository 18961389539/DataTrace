using System.Globalization;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 曲线判据求值：把特征集与判据逐项比对，输出可直接写入 NgReason 的中文原因。
/// 纯函数，便于在流水线里就地调用，也便于单测覆盖。
/// </summary>
public static class CurveCriterionEvaluator
{
    /// <summary>
    /// 判断判据是否作用于该序列。显式指定 SeriesName 时按名称匹配（忽略大小写与首尾空白）；
    /// 留空时作用于主序列（优先 Y 角色，否则第一条）。
    /// </summary>
    public static bool AppliesTo(CurveCriterion criterion, CurveSeries series, CurveSeries? primarySeries)
    {
        if (!criterion.Enabled)
        {
            return false;
        }

        var target = criterion.SeriesName?.Trim();
        if (!string.IsNullOrEmpty(target))
        {
            return string.Equals(target, series.Name?.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        if (primarySeries is null)
        {
            return false;
        }

        return primarySeries.Id != 0 && series.Id != 0
            ? primarySeries.Id == series.Id
            : ReferenceEquals(primarySeries, series);
    }

    /// <summary>取曲线的主序列：优先 Y 角色，否则第一条。没有序列时返回 null。</summary>
    public static CurveSeries? PrimarySeries(CurveDefinition curve)
        => curve.Series.FirstOrDefault(s => s.Role == SeriesRole.Y) ?? curve.Series.FirstOrDefault();

    /// <summary>逐项比对。返回全部违规描述，空列表表示合格。</summary>
    public static IReadOnlyList<string> Evaluate(CurveCriterion criterion, CurveFeatureSet feature)
    {
        var reasons = new List<string>();

        CheckRange(criterion.PeakMin, criterion.PeakMax, feature.Peak, "峰值", reasons);
        CheckRange(criterion.MeanMin, criterion.MeanMax, feature.Mean, "均值", reasons);
        CheckRange(criterion.AreaMin, criterion.AreaMax, feature.Area, "面积", reasons);
        CheckRange(criterion.RiseSlopeMin, criterion.RiseSlopeMax, feature.RiseSlope, "上升段斜率", reasons);
        CheckRange(criterion.HoldSlopeMin, criterion.HoldSlopeMax, feature.HoldSlope, "保压段斜率", reasons);

        if (criterion.FallRatioMax is { } fallRatioMax && feature.FallRatio > fallRatioMax)
        {
            reasons.Add($"回落比例 {Percent(feature.FallRatio)} 超过上限 {Percent(fallRatioMax)}");
        }

        if (criterion.MaxStepMax is { } maxStepMax && feature.MaxStep > maxStepMax)
        {
            reasons.Add($"单步跳变 {Number(feature.MaxStep)} 超过上限 {Number(maxStepMax)}");
        }

        if (criterion.StdDevMax is { } stdDevMax && feature.StdDev > stdDevMax)
        {
            reasons.Add($"波动标准差 {Number(feature.StdDev)} 超过上限 {Number(stdDevMax)}");
        }

        if (criterion.OscillationMax is { } oscillationMax && feature.Oscillations > oscillationMax)
        {
            reasons.Add($"振荡次数 {feature.Oscillations} 超过上限 {oscillationMax}");
        }

        return reasons;
    }

    private static void CheckRange(double? lower, double? upper, double value, string label, List<string> reasons)
    {
        if (lower is { } lo && value < lo)
        {
            reasons.Add($"{label} {Number(value)} 低于下限 {Number(lo)}");
        }
        else if (upper is { } hi && value > hi)
        {
            reasons.Add($"{label} {Number(value)} 超过上限 {Number(hi)}");
        }
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Percent(double value) => value.ToString("P1", CultureInfo.InvariantCulture);
}
