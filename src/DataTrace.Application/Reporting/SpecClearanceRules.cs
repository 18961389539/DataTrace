using System.Globalization;
using DataTrace.Application.Runtime;

namespace DataTrace.Application.Reporting;

/// <summary>
/// 还在规格限内的读数，离红线还有多远。只看直通率旁边那个点。
/// 规格带知道宽度时，靠红线的一成是带宽的十分之一。
/// </summary>
public static class SpecClearanceRules
{
    public const double NearBandFraction = 0.1;

    public static SpecClearance? Of(IReadOnlyList<InSpecReading> readings)
    {
        var margins = new List<(double Margin, double? Span)>();
        foreach (var reading in readings)
        {
            if (!TryMargin(reading, out var margin, out var span))
            {
                continue;
            }

            margins.Add((margin, span));
        }

        if (margins.Count == 0)
        {
            return null;
        }

        var nearest = margins.Min(item => item.Margin);
        var banded = margins.Where(item => item.Span is > 0).ToList();
        var near = banded.Count(item => item.Margin <= item.Span!.Value * NearBandFraction);
        var hasBand = banded.Count > 0;
        return new SpecClearance
        {
            Nearest = nearest,
            NearCount = near,
            HasBand = hasBand,
            Text = TextOf(nearest, near, hasBand)
        };
    }

    public static string TextOf(double nearest, int nearCount, bool hasBand)
    {
        var distance = nearest <= 1e-9 ? "已经贴着红线" : $"离红线还有 {FormatDistance(nearest)}";
        var head = $"合格件里最近{distance}";
        return hasBand && nearCount > 0 ? $"{head}，{nearCount} 件落在靠红线的一成内" : head;
    }

    private static bool TryMargin(InSpecReading reading, out double margin, out double? span)
    {
        margin = 0;
        span = null;
        var value = reading.Value;
        if (!double.IsFinite(value))
        {
            return false;
        }

        double? toUpper = reading.Upper is { } upper && value <= upper ? upper - value : null;
        double? toLower = reading.Lower is { } lower && value >= lower ? value - lower : null;
        if (reading.Upper is { } above && value > above)
        {
            return false;
        }

        if (reading.Lower is { } below && value < below)
        {
            return false;
        }

        if (toUpper is null && toLower is null)
        {
            return false;
        }

        margin = toUpper is null ? toLower!.Value : toLower is null ? toUpper.Value : Math.Min(toUpper.Value, toLower.Value);
        if (reading.Lower is { } low && reading.Upper is { } high && high > low)
        {
            span = high - low;
        }

        return true;
    }

    private static string FormatDistance(double value)
    {
        var abs = Math.Abs(value);
        var format = abs >= 100 ? "0" : abs >= 10 ? "0.0" : "0.##";
        return value.ToString(format, CultureInfo.InvariantCulture);
    }
}
