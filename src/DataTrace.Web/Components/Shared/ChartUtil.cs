using System.Globalization;

namespace DataTrace.Web.Components.Shared;

/// <summary>
/// SVG 绘图区：统一留白与坐标换算，避免各页面各写一套折线点计算。
/// </summary>
public readonly record struct ChartBox(
    double Width,
    double Height,
    double Left = 52,
    double Right = 14,
    double Top = 12,
    double Bottom = 26)
{
    public double PlotWidth => Math.Max(1, Width - Left - Right);

    public double PlotHeight => Math.Max(1, Height - Top - Bottom);

    public double X(int index, int count)
        => count <= 1 ? Left + PlotWidth / 2 : Left + index * PlotWidth / (count - 1);

    public double Y(double value, double min, double max)
    {
        var span = Math.Max(1e-9, max - min);
        return Top + PlotHeight - (value - min) / span * PlotHeight;
    }
}

/// <summary>折线图序列配色：取值统一由 <see cref="DtColors"/> 提供，与主题同一色系。</summary>
public static class ChartColors
{
    public const string Primary = DtColors.Primary;
    public const string Secondary = DtColors.SeriesAmber;
    public const string Third = DtColors.SeriesGreen;
    public const string Alert = DtColors.Danger;

    public static string ByIndex(int index) => (index % 3) switch
    {
        0 => Primary,
        1 => Secondary,
        _ => Third
    };

    public static string ForJudgement(bool isNg) => isNg ? Alert : Primary;
}

/// <summary>一条曲线：Values 为 Y 值，XValues 为空时按点序号作为 X。</summary>
public sealed class LineSeries
{
    public required string Name { get; init; }
    public required float[] Values { get; init; }
    public float[]? XValues { get; init; }
    public string Color { get; init; } = ChartColors.Primary;
    public string? Unit { get; init; }
}

/// <summary>折线图公共计算：量程、降采样、点数与网格刻度。</summary>
public static class ChartUtil
{
    /// <summary>取量程；全平数据也给出一点上下裕度，避免除零。</summary>
    public static (double Min, double Max) Range(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
        {
            return (0, 1);
        }

        double min = double.MaxValue;
        double max = double.MinValue;
        foreach (var value in values)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        if (Math.Abs(max - min) < 1e-9)
        {
            var pad = Math.Abs(max) < 1e-9 ? 1 : Math.Abs(max) * 0.05;
            return (min - pad, max + pad);
        }

        return (min, max);
    }

    public static (double Min, double Max) Range(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 1);
        }

        var min = values.Min();
        var max = values.Max();
        if (Math.Abs(max - min) < 1e-9)
        {
            var pad = Math.Abs(max) < 1e-9 ? 1 : Math.Abs(max) * 0.05;
            return (min - pad, max + pad);
        }

        return (min, max);
    }

    /// <summary>
    /// 按 X 分桶抽稀：每桶保留该桶的最小点与最大点（按 x 先后写入），并返回<b>抽稀前</b>的真实极值。
    /// </summary>
    /// <remarks>
    /// 就近取点会把尖峰整条抹掉，而压力曲线与 SPC 要看的恰恰是尖峰。
    /// 极值也必须在抽稀之前算：否则图例上的最小/最大不是真实最值，
    /// Y 轴还会按抽稀后的量程画，把尖峰裁到画框外面。
    /// </remarks>
    public static (float[] X, float[] Y, double Min, double Max) SampleEnvelope(
        float[] values, float[] xs, int maxPoints)
    {
        var count = values.Length;
        var (min, max) = Range(values);

        if (count == 0 || count <= maxPoints)
        {
            return (xs, values, min, max);
        }

        var buckets = Math.Max(1, maxPoints / 2);
        var outX = new List<float>(buckets * 2 + 2);
        var outY = new List<float>(buckets * 2 + 2);
        var size = (double)count / buckets;

        for (var b = 0; b < buckets; b++)
        {
            var from = (int)(b * size);
            var to = Math.Min(count - 1, (int)((b + 1) * size) - 1);
            if (to < from)
            {
                continue;
            }

            var low = from;
            var high = from;
            for (var i = from; i <= to; i++)
            {
                if (values[i] < values[low])
                {
                    low = i;
                }

                if (values[i] > values[high])
                {
                    high = i;
                }
            }

            var first = Math.Min(low, high);
            var second = Math.Max(low, high);
            outX.Add(xs[first]);
            outY.Add(values[first]);
            if (second != first)
            {
                outX.Add(xs[second]);
                outY.Add(values[second]);
            }
        }

        // 末点补齐，曲线右端不会被截短一截。
        if (outX.Count == 0 || outX[^1] != xs[count - 1])
        {
            outX.Add(xs[count - 1]);
            outY.Add(values[count - 1]);
        }

        return (outX.ToArray(), outY.ToArray(), min, max);
    }

    /// <summary>把一列数值转成 SVG polyline 的 points 属性。</summary>
    public static string Polyline(IReadOnlyList<float> values, in ChartBox box, double min, double max)
    {
        if (values.Count == 0)
        {
            return "";
        }

        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(Format(box.X(i, values.Count)));
            builder.Append(',');
            builder.Append(Format(box.Y(values[i], min, max)));
        }

        return builder.ToString();
    }

    /// <summary>Y 轴 5 档刻度值（从大到小）。</summary>
    public static IReadOnlyList<double> Ticks(double min, double max, int count = 5)
    {
        var span = max - min;
        if (count < 2)
        {
            count = 2;
        }

        var ticks = new double[count];
        for (var i = 0; i < count; i++)
        {
            ticks[i] = min + span * i / (count - 1);
        }

        return ticks;
    }

    /// <summary>刻度文本：大数走科学计数法的简化写法，保持标签短。</summary>
    public static string Label(double value)
    {
        var abs = Math.Abs(value);
        if (abs >= 100000)
        {
            return value.ToString("0.0E+0", CultureInfo.InvariantCulture);
        }

        var digits = abs >= 100 ? 0 : abs >= 1 ? 1 : 3;
        return value.ToString("F" + digits, CultureInfo.InvariantCulture);
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
