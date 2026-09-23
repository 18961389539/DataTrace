using System.Globalization;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 单值-移动极差（I-MR）控制图与过程能力指数计算。
/// 纯函数、无副作用：一个托盘一个测量值的场景没有子组可划，Xbar-R 用不上，
/// I-MR 才是对得上的工具。
/// </summary>
/// <remarks>
/// 常数取自 n=2 的极差法标准表：
/// d2 = 1.128（组内 σ 估计用 MR̄/d2）、E2 = 3/d2 ≈ 2.66（X 图控制限）、D4 = 3.267（MR 图上限）。
/// 所有能力指数在分母无效时返回 null，不返回 NaN/Infinity。
/// </remarks>
public static class SpcCalculator
{
    /// <summary>给出结论所需的最少样本数。低于此值仍然算 Cpk，但结论标记为"样本不足"。</summary>
    public const int MinimumSamplesForVerdict = 30;

    /// <summary>Cpk 达标线。</summary>
    public const double CapableThreshold = 1.33;

    /// <summary>Cpk 勉强线。</summary>
    public const double MarginalThreshold = 1.0;

    private const double D2 = 1.128;
    private const double E2 = 3.0 / D2;
    private const double D4 = 3.267;
    private const double Epsilon = 1e-12;

    public static SpcSummary Compute(
        IReadOnlyList<double> values,
        double? lowerSpec = null,
        double? upperSpec = null,
        double? targetValue = null)
    {
        var hasSpec = lowerSpec is not null || upperSpec is not null;

        if (values is null || values.Count == 0)
        {
            return new SpcSummary
            {
                HasSpecLimits = hasSpec,
                Verdict = SpcVerdict.InsufficientData,
                Note = "区间内没有采样数据"
            };
        }

        var n = values.Count;
        var mean = values.Average();
        var min = values.Min();
        var max = values.Max();

        var overallStdDev = n > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (n - 1)) : 0;

        // 移动极差：相邻两点的绝对差，共 n-1 个。
        var movingRangeMean = 0d;
        if (n > 1)
        {
            var sum = 0d;
            for (var i = 1; i < n; i++)
            {
                sum += Math.Abs(values[i] - values[i - 1]);
            }

            movingRangeMean = sum / (n - 1);
        }

        var withinStdDev = movingRangeMean / D2;
        var centerLine = mean;
        var upperControlLimit = mean + E2 * movingRangeMean;
        var lowerControlLimit = mean - E2 * movingRangeMean;

        var withinUsable = withinStdDev > Epsilon;
        var overallUsable = overallStdDev > Epsilon;

        double? cp = null;
        double? cpu = null;
        double? cpl = null;
        double? cpk = null;
        if (withinUsable)
        {
            if (lowerSpec is { } lsl && upperSpec is { } usl)
            {
                cp = (usl - lsl) / (6 * withinStdDev);
            }

            if (upperSpec is { } upper)
            {
                cpu = (upper - mean) / (3 * withinStdDev);
            }

            if (lowerSpec is { } lower)
            {
                cpl = (mean - lower) / (3 * withinStdDev);
            }

            cpk = Min(cpu, cpl);
        }

        double? pp = null;
        double? ppk = null;
        if (overallUsable)
        {
            if (lowerSpec is { } lsl && upperSpec is { } usl)
            {
                pp = (usl - lsl) / (6 * overallStdDev);
            }

            var ppu = upperSpec is { } upper ? (upper - mean) / (3 * overallStdDev) : (double?)null;
            var ppl = lowerSpec is { } lower ? (mean - lower) / (3 * overallStdDev) : (double?)null;
            ppk = Min(ppu, ppl);
        }

        var (verdict, note) = Judge(n, hasSpec, cpk);
        var offsetBasis = targetValue ?? (lowerSpec is { } a && upperSpec is { } b ? (a + b) / 2 : null);
        var meanOffset = offsetBasis is { } reference && withinUsable
            ? (mean - reference) / withinStdDev
            : (double?)null;

        return new SpcSummary
        {
            Count = n,
            Mean = mean,
            Min = min,
            Max = max,
            OverallStdDev = overallStdDev,
            WithinStdDev = withinStdDev,
            MovingRangeMean = movingRangeMean,
            MovingRangeUpperLimit = D4 * movingRangeMean,
            CenterLine = centerLine,
            UpperControlLimit = upperControlLimit,
            LowerControlLimit = lowerControlLimit,
            Cp = cp,
            Cpu = cpu,
            Cpl = cpl,
            Cpk = cpk,
            Pp = pp,
            Ppk = ppk,
            HasSpecLimits = hasSpec,
            Verdict = verdict,
            Note = note,
            MeanOffsetInSigma = meanOffset
        };
    }

    /// <summary>取可用的最小值；两个都为 null 时返回 null。</summary>
    private static double? Min(double? left, double? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return Math.Min(left.Value, right.Value);
    }

    private static (SpcVerdict Verdict, string? Note) Judge(int count, bool hasSpec, double? cpk)
    {
        if (!hasSpec)
        {
            return (SpcVerdict.InsufficientData, "该点位没有配置规格限，无法评估过程能力");
        }

        if (cpk is null)
        {
            // 只有样本波动恒为 0 才会走到这里。
            return (SpcVerdict.InsufficientData, "样本波动为 0，无法评估过程能力（可能点位未刷新或读了常量寄存器）");
        }

        if (count < MinimumSamplesForVerdict)
        {
            // 数字照给，但结论不下：小样本的 Cpk 会过度乐观。
            return (SpcVerdict.InsufficientData,
                $"样本不足 {MinimumSamplesForVerdict} 个（当前 {count} 个），Cpk 只作趋势参考");
        }

        if (cpk < MarginalThreshold)
        {
            return (SpcVerdict.Incapable, null);
        }

        return cpk < CapableThreshold
            ? (SpcVerdict.Marginal, null)
            : (SpcVerdict.Capable, null);
    }

    /// <summary>把能力指数格式化成界面用文本；null 显示为破折号。</summary>
    public static string Format(double? value, string format = "0.00")
        => value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "-";
}
