namespace DataTrace.Web.Components.Shared;

/// <summary>
/// 采集耗时的显示格式。模拟器跑出来的单件耗时常常只有几十毫秒，
/// 一律按秒格式化会变成「0.0 s」，等于把节拍这个数废掉。
/// </summary>
public static class DurationFormat
{
    /// <summary>毫秒级给 ms，秒级以上给 s。</summary>
    public static string Text(int? durationMs)
    {
        if (durationMs is null or <= 0) return "—";
        return durationMs.Value < 1000
            ? $"{durationMs.Value} ms"
            : $"{durationMs.Value / 1000.0:0.0} s";
    }

    /// <summary>一组耗时的均值；一台都没采过时给「—」。</summary>
    public static string Average(IEnumerable<int?> durations)
    {
        var samples = durations.Where(ms => ms is > 0).Select(ms => ms!.Value).ToList();
        if (samples.Count == 0) return "—";
        var avg = samples.Average();
        return avg < 1000 ? $"{avg:0} ms" : $"{avg / 1000.0:0.0} s";
    }
}
