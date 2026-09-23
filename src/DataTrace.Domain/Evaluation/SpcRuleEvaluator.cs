namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 控制图判异。已实现 Nelson / Western Electric 规则 1–5（现场最常命中的五条）；
/// 规则 6–8（4/5 点超 1σ、15 点在 1σ 内、8 点在 1σ 外）判断价值低、误报多，暂不实现。
/// </summary>
/// <remarks>
/// 纯函数：<paramref name="summary"/> 必须是同一组 <paramref name="values"/> 算出来的，
/// 本类不做一致性校验，乱传会得出无意义的结论。
/// 每条规则会把相邻/重叠的命中窗口合并成一段，避免连续漂移产生几十条重复告警。
/// </remarks>
public static class SpcRuleEvaluator
{
    /// <summary>规则 2 的连续点数阈值。</summary>
    public const int SameSideRunLength = 9;

    /// <summary>规则 3 的连续点数阈值。</summary>
    public const int MonotonicRunLength = 6;

    /// <summary>规则 4 的连续点数阈值。</summary>
    public const int AlternatingRunLength = 14;

    /// <summary>规则 5 的窗口长度。</summary>
    public const int TwoOfThreeWindowLength = 3;

    public static IReadOnlyList<SpcViolation> Evaluate(IReadOnlyList<double> values, SpcSummary summary)
    {
        var violations = new List<SpcViolation>();
        if (values is null || values.Count == 0)
        {
            return violations;
        }

        var n = values.Count;
        var centerLine = summary.CenterLine;
        var sigma = summary.WithinStdDev;

        DetectBeyondControlLimit(values, summary, violations);
        DetectSameSideRun(values, centerLine, violations);
        DetectMonotonicRun(values, violations);
        DetectAlternatingRun(values, violations);
        DetectTwoOfThreeBeyondTwoSigma(values, centerLine, sigma, violations);

        return violations;
    }

    /// <summary>规则 1：单点超出控制限。</summary>
    private static void DetectBeyondControlLimit(IReadOnlyList<double> values, SpcSummary summary, List<SpcViolation> target)
    {
        var anchors = new List<int>();
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] > summary.UpperControlLimit || values[i] < summary.LowerControlLimit)
            {
                anchors.Add(i);
            }
        }

        Emit(target, SpcRule.BeyondControlLimit, anchors, 1, count => $"超出控制限（连续 {count} 点）");
    }

    /// <summary>规则 2：连续 9 点落在中心线同一侧（等于中心线会打断连续）。</summary>
    private static void DetectSameSideRun(IReadOnlyList<double> values, double centerLine, List<SpcViolation> target)
    {
        var anchors = new List<int>();
        for (var i = 0; i + SameSideRunLength - 1 < values.Count; i++)
        {
            var side = Side(values[i], centerLine);
            if (side == 0)
            {
                continue;
            }

            var matched = true;
            for (var offset = 1; offset < SameSideRunLength; offset++)
            {
                if (Side(values[i + offset], centerLine) != side)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                anchors.Add(i);
            }
        }

        Emit(target, SpcRule.NineOnOneSide, anchors, SameSideRunLength,
            count => $"中心线同侧连续 {count} 点（阈值 {SameSideRunLength}）");
    }

    /// <summary>规则 3：连续 6 点单调递增或递减。</summary>
    private static void DetectMonotonicRun(IReadOnlyList<double> values, List<SpcViolation> target)
    {
        var anchors = new List<int>();
        for (var i = 0; i + MonotonicRunLength - 1 < values.Count; i++)
        {
            var increasing = true;
            var decreasing = true;
            for (var offset = 1; offset < MonotonicRunLength; offset++)
            {
                if (values[i + offset] <= values[i + offset - 1])
                {
                    increasing = false;
                }

                if (values[i + offset] >= values[i + offset - 1])
                {
                    decreasing = false;
                }
            }

            if (increasing || decreasing)
            {
                anchors.Add(i);
            }
        }

        Emit(target, SpcRule.SixMonotonic, anchors, MonotonicRunLength,
            count => $"连续 {count} 点持续单调变化（阈值 {MonotonicRunLength}，趋势）");
    }

    /// <summary>规则 4：连续 14 点上下交替（相邻步方向相反，出现等值即打断）。</summary>
    private static void DetectAlternatingRun(IReadOnlyList<double> values, List<SpcViolation> target)
    {
        var anchors = new List<int>();
        for (var i = 0; i + AlternatingRunLength - 1 < values.Count; i++)
        {
            var matched = true;
            for (var step = 1; step + 1 < AlternatingRunLength; step++)
            {
                var first = Math.Sign(values[i + step] - values[i + step - 1]);
                var second = Math.Sign(values[i + step + 1] - values[i + step]);
                if (first == 0 || second == 0 || first == second)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                anchors.Add(i);
            }
        }

        Emit(target, SpcRule.FourteenAlternating, anchors, AlternatingRunLength,
            count => $"连续 {count} 点上下交替（阈值 {AlternatingRunLength}，系统性振荡）");
    }

    /// <summary>规则 5：任意连续 3 点中有 2 点同侧超出 ±2σ。</summary>
    private static void DetectTwoOfThreeBeyondTwoSigma(
        IReadOnlyList<double> values,
        double centerLine,
        double sigma,
        List<SpcViolation> target)
    {
        var upper = centerLine + 2 * sigma;
        var lower = centerLine - 2 * sigma;

        var anchors = new List<int>();
        for (var i = 0; i + TwoOfThreeWindowLength - 1 < values.Count; i++)
        {
            var above = 0;
            var below = 0;
            for (var offset = 0; offset < TwoOfThreeWindowLength; offset++)
            {
                if (values[i + offset] > upper)
                {
                    above++;
                }
                else if (values[i + offset] < lower)
                {
                    below++;
                }
            }

            if (above >= 2 || below >= 2)
            {
                anchors.Add(i);
            }
        }

        Emit(target, SpcRule.TwoOfThreeBeyondTwoSigma, anchors, TwoOfThreeWindowLength,
            count => $"连续 {count} 点中出现同侧超出 ±2σ 的聚集（窗口 {TwoOfThreeWindowLength}）");
    }

    private static int Side(double value, double centerLine)
        => value > centerLine ? 1 : value < centerLine ? -1 : 0;

    /// <summary>
    /// 把命中窗口的起点序列合并成连续区段，每段只产出一条违规。
    /// 起点连续（后一个 = 前一个 + 1）说明窗口在滑动中持续命中，应当合并成一段，
    /// 否则一次 20 点的漂移会输出十几条重复告警。
    /// </summary>
    private static void Emit(
        List<SpcViolation> target,
        SpcRule rule,
        List<int> anchors,
        int windowLength,
        Func<int, string> describe)
    {
        if (anchors.Count == 0)
        {
            return;
        }

        var start = anchors[0];
        var previous = anchors[0];
        foreach (var anchor in anchors.Skip(1))
        {
            if (anchor == previous + 1)
            {
                previous = anchor;
                continue;
            }

            Add(target, rule, start, previous + windowLength - 1, describe);
            start = anchor;
            previous = anchor;
        }

        Add(target, rule, start, previous + windowLength - 1, describe);

        static void Add(List<SpcViolation> target, SpcRule rule, int start, int end, Func<int, string> describe)
        {
            var count = end - start + 1;
            target.Add(new SpcViolation
            {
                Rule = rule,
                Description = describe(count),
                StartIndex = start,
                EndIndex = end
            });
        }
    }
}
