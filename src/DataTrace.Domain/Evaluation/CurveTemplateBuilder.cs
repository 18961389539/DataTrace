using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 从历史合格曲线特征构建波形基线模板。纯函数、无副作用。
/// </summary>
/// <remarks>
/// 用中位数 + MAD 而不是均值 + 标准差作为中心与离散度。
/// 理由：历史样本里混进几条不良曲线是常态，均值基线会被整体拽偏，
/// 导致模板本身就失真；中位数对离群样本不敏感，MAD 同理。
/// 均值仍然留在结果里供人查看，但打分不使用它。
/// </remarks>
public static class CurveTemplateBuilder
{
    /// <summary>模板可靠所需的最小样本数。低于此值仍然给模板，但结论标记为不可用。</summary>
    public const int MinimumReliableSamples = 20;

    /// <summary>MAD 到正态 σ 的一致性系数（1 / Φ⁻¹(0.75)）。</summary>
    public const double MadToSigma = 1.4826;

    /// <summary>识别离群样本的稳健倍数（|x - 中位数| &gt; 该倍数 × σ）。</summary>
    public const double OutlierSigma = 3;

    /// <summary>判定"零波动维度"时，σ 相对中心的容许下限。</summary>
    private const double ConstantAbsoluteEpsilon = 1e-12;

    private const double ConstantRelativeEpsilon = 1e-9;

    /// <summary>用合格样本构建模板。样本为空时返回全维度零样本的模板。</summary>
    public static CurveTemplate Build(IReadOnlyList<CurveFeature> samples)
    {
        if (samples is null || samples.Count == 0)
        {
            return new CurveTemplate
            {
                SampleCount = 0,
                ScorableDimensionCount = 0,
                IsReliable = false,
                Note = $"没有可用于建立基线的合格样本（至少需要 {MinimumReliableSamples} 条）。",
                Dimensions = CurveFeatureDimensions.All.Select(d => new CurveDimensionBaseline { Dimension = d }).ToList()
            };
        }

        var dimensions = new List<CurveDimensionBaseline>(CurveFeatureDimensions.All.Count);
        foreach (var dimension in CurveFeatureDimensions.All)
        {
            dimensions.Add(BuildDimension(dimension, samples));
        }

        var scorable = dimensions.Count(d => d.IsScorable);
        var reliable = samples.Count >= MinimumReliableSamples && scorable > 0;

        return new CurveTemplate
        {
            SampleCount = samples.Count,
            ScorableDimensionCount = scorable,
            IsReliable = reliable,
            Note = BuildNote(samples.Count, scorable, reliable),
            Dimensions = dimensions
        };
    }

    private static CurveDimensionBaseline BuildDimension(
        CurveFeatureDimension dimension,
        IReadOnlyList<CurveFeature> samples)
    {
        var values = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            values[i] = CurveFeatureDimensions.Read(samples[i], dimension);
        }

        Array.Sort(values);

        var center = Median(values);
        var sigma = MadToSigma * MedianAbsoluteDeviation(values, center);

        var constant = sigma <= Math.Max(ConstantAbsoluteEpsilon, Math.Abs(center) * ConstantRelativeEpsilon);
        if (constant)
        {
            // 零波动：置 0 而不是留一个 1e-300 量级的数，避免下游算出天文数字的 z 分数。
            sigma = 0;
        }

        var outlierCount = constant
            ? 0
            : values.Count(v => Math.Abs(v - center) > OutlierSigma * sigma);

        double sum = 0;
        foreach (var v in values)
        {
            sum += v;
        }

        return new CurveDimensionBaseline
        {
            Dimension = dimension,
            Count = values.Length,
            Center = center,
            Sigma = sigma,
            Mean = sum / values.Length,
            Min = values[0],
            Max = values[^1],
            OutlierCount = outlierCount,
            IsConstant = constant
        };
    }

    private static string? BuildNote(int sampleCount, int scorable, bool reliable)
    {
        if (reliable)
        {
            return $"基线由 {sampleCount} 条合格样本建立，{scorable} 个维度可参与比对。";
        }

        if (scorable == 0)
        {
            return $"全部维度均无波动（{sampleCount} 条样本），无法建立可用于比对的基线；" +
                   "请确认曲线点位是否真的在刷新。";
        }

        return $"样本只有 {sampleCount} 条，少于可靠基线所需的 {MinimumReliableSamples} 条，" +
               "偏离分仅作参考，不建议据此下结论。";
    }

    /// <summary>中位数（偶数个取中间两个的均值）。values 必须已升序。</summary>
    internal static double Median(IReadOnlyList<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>中位数绝对偏差（未乘一致性系数）。values 必须已升序。</summary>
    private static double MedianAbsoluteDeviation(IReadOnlyList<double> sorted, double center)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var deviations = new double[sorted.Count];
        for (var i = 0; i < sorted.Count; i++)
        {
            deviations[i] = Math.Abs(sorted[i] - center);
        }

        Array.Sort(deviations);
        return Median(deviations);
    }
}
