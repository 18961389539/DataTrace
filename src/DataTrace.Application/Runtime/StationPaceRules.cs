namespace DataTrace.Application.Runtime;

/// <summary>
/// 同一工站相邻两件完成相隔多久。看板上取间隔最长的那一站：
/// 这一站做一件要隔多久，这一班最多也就能做多少件。
/// 采集耗时（读数花了多久）不算在这里。
/// </summary>
public static class StationPaceRules
{
    public readonly record struct Sample(string StationCode, int Sequence, bool Enabled, TimeSpan? Gap);

    public readonly record struct Pace(string StationCode, TimeSpan Gap)
    {
        public string Text => Format(Gap);

        public string Hint => $"最慢在 {StationCode}";
    }

    public static Pace? Slowest(IEnumerable<Sample> stations)
    {
        var ranked = stations
            .Where(station => station.Enabled && station.Gap is { } gap && gap > TimeSpan.Zero)
            .OrderByDescending(station => station.Gap)
            .ThenBy(station => station.Sequence)
            .ThenBy(station => station.StationCode, StringComparer.Ordinal)
            .FirstOrDefault();
        if (ranked.Gap is not { } gap || gap <= TimeSpan.Zero)
        {
            return null;
        }

        var code = string.IsNullOrWhiteSpace(ranked.StationCode)
            ? ranked.Sequence.ToString()
            : ranked.StationCode.Trim();
        return new Pace(code, gap);
    }

    public static string Format(TimeSpan gap)
    {
        if (gap <= TimeSpan.Zero)
        {
            return "—";
        }

        if (gap.TotalSeconds < 10)
        {
            return gap.TotalMilliseconds < 1000
                ? $"{gap.TotalMilliseconds:0} ms"
                : $"{gap.TotalSeconds:0.0} 秒";
        }

        var totalSeconds = (int)Math.Round(gap.TotalSeconds, MidpointRounding.AwayFromZero);
        if (totalSeconds < 60)
        {
            return $"{totalSeconds} 秒";
        }

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        if (minutes < 60)
        {
            return seconds == 0 ? $"{minutes} 分钟" : $"{minutes} 分 {seconds} 秒";
        }

        var hours = minutes / 60;
        var restMinutes = minutes % 60;
        return restMinutes == 0 ? $"{hours} 小时" : $"{hours} 小时 {restMinutes} 分钟";
    }
}
