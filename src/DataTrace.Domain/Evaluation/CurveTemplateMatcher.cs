using DataTrace.Domain.Entities;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 把一条曲线特征与基线模板比对，给出偏离分与结论。纯函数、无副作用。
/// </summary>
/// <remarks>
/// 打分采用各维度 z 分数的均方根（RMS）。z 分数以稳健中心/离散度为基准，
/// 因此量纲无关 —— 峰值（几十 kN）与回落比例（0~1）可以直接放在同一个分数里比较。
/// </remarks>
public static class CurveTemplateMatcher
{
    /// <summary>单维偏离达到该倍数即视为"显著偏离"，计入明细。</summary>
    public const double SuspiciousZ = 2.0;

    /// <summary>综合偏离分达到该倍数即判定异常。</summary>
    public const double AbnormalZ = 3.0;

    /// <summary>单维偏离达到该倍数即判定异常（一维极端偏离足以定性）。</summary>
    public const double AbnormalMaxZ = 4.0;

    public static CurveTemplateScore Score(CurveTemplate template, CurveFeature feature)
        => Score(template, feature, curveRecordId: 0, time: default, palletCode: "", isNg: false);

    /// <summary>
    /// 对单条曲线特征打分。
    /// 模板不可靠时仍然给出偏离数字，但结论固定为 <see cref="CurveTemplateVerdict.InsufficientBaseline"/>，
    /// 由调用方决定怎么展示"仅供参考"。
    /// </summary>
    public static CurveTemplateScore Score(
        CurveTemplate template,
        CurveFeature feature,
        long curveRecordId,
        DateTime time,
        string palletCode,
        bool isNg)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(feature);

        var deviations = new List<CurveTemplateDeviation>();
        var constantBreaches = 0;
        double sumSquares = 0;
        var scoredCount = 0;
        var maxAbsZ = 0d;
        CurveFeatureDimension? worst = null;

        foreach (var baseline in template.Dimensions)
        {
            var value = CurveFeatureDimensions.Read(feature, baseline.Dimension);

            if (baseline.IsConstant)
            {
                // 零波动维度：不参与 RMS（否则 z 分数会除零爆炸），
                // 但对连续量而言"从来没变过的值变了"本身就是信号。
                if (CurveFeatureDimensions.IsContinuous(baseline.Dimension)
                    && baseline.Count > 1
                    && Math.Abs(value - baseline.Center) > baseline.ConstantTolerance)
                {
                    constantBreaches++;
                    deviations.Add(new CurveTemplateDeviation
                    {
                        Dimension = baseline.Dimension,
                        Value = value,
                        BaselineCenter = baseline.Center,
                        BaselineSigma = 0,
                        ZScore = null,
                        IsConstantBreach = true
                    });
                }

                continue;
            }

            if (!baseline.IsScorable)
            {
                continue;
            }

            var z = (value - baseline.Center) / baseline.Sigma;
            scoredCount++;
            sumSquares += z * z;

            var abs = Math.Abs(z);
            if (abs > maxAbsZ)
            {
                maxAbsZ = abs;
                worst = baseline.Dimension;
            }

            var deviation = new CurveTemplateDeviation
            {
                Dimension = baseline.Dimension,
                Value = value,
                BaselineCenter = baseline.Center,
                BaselineSigma = baseline.Sigma,
                ZScore = z,
                IsConstantBreach = false
            };
            if (deviation.IsDeviating)
            {
                deviations.Add(deviation);
            }
        }

        var rmsZ = scoredCount > 0 ? Math.Sqrt(sumSquares / scoredCount) : 0;

        return new CurveTemplateScore
        {
            CurveRecordId = curveRecordId,
            Time = time,
            PalletCode = palletCode,
            IsNg = isNg,
            RmsZ = rmsZ,
            MaxAbsZ = maxAbsZ,
            WorstDimension = worst,
            ConstantBreachCount = constantBreaches,
            Verdict = Verdict(template, scoredCount, rmsZ, maxAbsZ, constantBreaches),
            ScoredDimensionCount = scoredCount,
            Deviations = deviations
                .OrderByDescending(d => d.IsConstantBreach ? double.MaxValue : Math.Abs(d.ZScore ?? 0))
                .ToList()
        };
    }

    private static CurveTemplateVerdict Verdict(
        CurveTemplate template,
        int scoredCount,
        double rmsZ,
        double maxAbsZ,
        int constantBreaches)
    {
        // 模板本身立不住时不下任何结论 —— 给一个"看起来正常/异常"的判定比不给更危险。
        if (!template.IsReliable || scoredCount == 0)
        {
            return CurveTemplateVerdict.InsufficientBaseline;
        }

        if (rmsZ >= AbnormalZ || maxAbsZ >= AbnormalMaxZ || constantBreaches >= 2)
        {
            return CurveTemplateVerdict.Abnormal;
        }

        if (rmsZ >= SuspiciousZ || maxAbsZ >= SuspiciousZ || constantBreaches == 1)
        {
            return CurveTemplateVerdict.Suspicious;
        }

        return CurveTemplateVerdict.Normal;
    }
}
