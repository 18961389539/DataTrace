using DataTrace.Domain.Evaluation;

namespace DataTrace.Application.Alarms;

/// <summary>一个点位在某一件上的观测，只用于重算预警和过程漂移。</summary>
public sealed class TagObservation
{
    public int StationId { get; set; }

    public string StationCode { get; set; } = "";

    public int TagId { get; set; }

    public string TagName { get; set; } = "";

    public DateTime CompleteTime { get; set; }

    public bool IsWarning { get; set; }

    public bool IsOutOfLimit { get; set; }

    public double? NumericValue { get; set; }
}

/// <summary>同一点位连续落在预警带的件数。合格或超限会把它清掉。</summary>
public readonly record struct TagWarningStreak(int StationId, string StationCode, int TagId, string TagName, int Count);

/// <summary>控制图此刻仍命中的过程漂移。只保留规则 1（超出控制限）和规则 2（单侧连续）。</summary>
public readonly record struct TagDriftNotice(int StationId, string StationCode, int TagId, string TagName, string Detail);

/// <summary>
/// 预警连续件数和过程漂移。纯函数，采集发布和启动重算用同一份。
/// 过程漂移只看每个点位最近 <see cref="DriftWindow"/> 个实测值，并且违规必须延伸到最新一点。
/// </summary>
public static class TagWatchRules
{
    public const int DriftWindow = 30;

    public static void ApplyWarning(
        IDictionary<(int StationId, int TagId), TagWarningStreak> streaks,
        int stationId,
        string stationCode,
        int tagId,
        string tagName,
        bool isWarning,
        bool isOutOfLimit)
    {
        var key = (stationId, tagId);
        if (!isWarning || isOutOfLimit)
        {
            streaks.Remove(key);
            return;
        }

        var code = string.IsNullOrWhiteSpace(stationCode) ? stationId.ToString() : stationCode;
        var name = string.IsNullOrWhiteSpace(tagName) ? tagId.ToString() : tagName;
        var count = streaks.TryGetValue(key, out var current) ? current.Count + 1 : 1;
        streaks[key] = new TagWarningStreak(stationId, code, tagId, name, count);
    }

    public static TagDriftNotice? DriftOf(
        int stationId,
        string stationCode,
        int tagId,
        string tagName,
        IReadOnlyList<double> values)
    {
        if (values.Count < SpcRuleEvaluator.SameSideRunLength)
        {
            return null;
        }

        var window = values.Count <= DriftWindow ? values : values.Skip(values.Count - DriftWindow).ToList();
        var summary = SpcCalculator.Compute(window);
        var last = window.Count - 1;
        var current = SpcRuleEvaluator.Evaluate(window, summary)
            .Where(item => item.EndIndex == last
                           && item.Rule is SpcRule.BeyondControlLimit or SpcRule.NineOnOneSide)
            .ToList();
        if (current.Count == 0)
        {
            return null;
        }

        var rule = current.Any(item => item.Rule == SpcRule.BeyondControlLimit)
            ? SpcRule.BeyondControlLimit
            : SpcRule.NineOnOneSide;
        var code = string.IsNullOrWhiteSpace(stationCode) ? stationId.ToString() : stationCode;
        var name = string.IsNullOrWhiteSpace(tagName) ? tagId.ToString() : tagName;
        var detail = rule == SpcRule.BeyondControlLimit ? "最新一点超出控制限" : "最新连续多点落在中心线同一侧";
        return new TagDriftNotice(stationId, code, tagId, name, detail);
    }

    public static void Append(List<double> series, double value)
    {
        series.Add(value);
        if (series.Count > DriftWindow)
        {
            series.RemoveRange(0, series.Count - DriftWindow);
        }
    }
}
