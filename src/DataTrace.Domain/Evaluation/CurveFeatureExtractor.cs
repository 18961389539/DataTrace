using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Evaluation;

/// <summary>
/// 从曲线采样值提取特征。纯函数、无副作用、不依赖任何基础设施，
/// 因此可以在采集流水线里就地对已经在手的 payload 计算，不需要额外 IO。
/// </summary>
public static class CurveFeatureExtractor
{
    /// <summary>起升点判定阈值：min + 10% 量程。</summary>
    public const double RiseThresholdRatio = 0.1;

    private const double SlopeEpsilon = 1e-12;

    /// <summary>提取特征。values 为空时返回全 0 特征集。</summary>
    public static CurveFeatureSet Extract(IReadOnlyList<float> values)
    {
        if (values is null || values.Count == 0)
        {
            return new CurveFeatureSet();
        }

        var n = values.Count;
        double min = values[0];
        double peak = values[0];
        var minIndex = 0;
        var peakIndex = 0;
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var v = values[i];
            if (v < min)
            {
                min = v;
                minIndex = i;
            }

            if (v > peak)
            {
                peak = v;
                peakIndex = i;
            }

            sum += v;
        }

        var mean = sum / n;

        double squared = 0;
        for (var i = 0; i < n; i++)
        {
            var d = values[i] - mean;
            squared += d * d;
        }

        var stdDev = Math.Sqrt(squared / n);

        double area = 0;
        double maxStep = 0;
        var oscillations = 0;
        for (var i = 0; i + 1 < n; i++)
        {
            area += (values[i] + values[i + 1]) / 2.0;

            var step = Math.Abs((double)values[i + 1] - values[i]);
            if (step > maxStep)
            {
                maxStep = step;
            }

            var current = values[i] - mean;
            var next = values[i + 1] - mean;
            if ((current < 0 && next >= 0) || (current > 0 && next <= 0))
            {
                oscillations++;
            }
        }

        var range = peak - min;
        var riseThreshold = min + range * RiseThresholdRatio;
        var riseIndex = 0;
        for (var i = 0; i < n; i++)
        {
            if (values[i] >= riseThreshold)
            {
                riseIndex = i;
                break;
            }
        }

        return new CurveFeatureSet
        {
            PointCount = n,
            Min = min,
            MinIndex = minIndex,
            Peak = peak,
            PeakIndex = peakIndex,
            Mean = mean,
            StdDev = stdDev,
            Area = area,
            RiseSlope = Slope(values, 0, peakIndex),
            HoldSlope = Slope(values, peakIndex, n - 1),
            RiseIndex = riseIndex,
            RiseSpan = Math.Max(0, peakIndex - riseIndex),
            FallRatio = range > 0 ? (peak - values[n - 1]) / range : 0,
            MaxStep = maxStep,
            Oscillations = oscillations
        };
    }

    /// <summary>把特征集转成可落库的实体行。</summary>
    public static CurveFeature ToEntity(string seriesName, SeriesRole role, CurveFeatureSet set)
        => new()
        {
            SeriesName = seriesName,
            Role = role,
            PointCount = set.PointCount,
            Min = set.Min,
            MinIndex = set.MinIndex,
            Peak = set.Peak,
            PeakIndex = set.PeakIndex,
            Mean = set.Mean,
            StdDev = set.StdDev,
            Area = set.Area,
            RiseSlope = set.RiseSlope,
            HoldSlope = set.HoldSlope,
            RiseIndex = set.RiseIndex,
            RiseSpan = set.RiseSpan,
            FallRatio = set.FallRatio,
            MaxStep = set.MaxStep,
            Oscillations = set.Oscillations
        };

    /// <summary>
    /// 对闭区间 [from, to] 做最小二乘斜率，横轴为相对序号（以 from 为原点，
    /// 避免大序号输入带来的浮点误差）。点数不足 2 或分母退化时返回 0。
    /// </summary>
    private static double Slope(IReadOnlyList<float> values, int from, int to)
    {
        var count = to - from + 1;
        if (count < 2)
        {
            return 0;
        }

        double sumX = 0;
        double sumY = 0;
        double sumXx = 0;
        double sumXy = 0;
        for (var i = 0; i < count; i++)
        {
            double x = i;
            double y = values[from + i];
            sumX += x;
            sumY += y;
            sumXx += x * x;
            sumXy += x * y;
        }

        var denominator = count * sumXx - sumX * sumX;
        if (Math.Abs(denominator) < SlopeEpsilon)
        {
            return 0;
        }

        return (count * sumXy - sumX * sumY) / denominator;
    }
}
