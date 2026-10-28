namespace DataTrace.Application.Alarms;

/// <summary>从报警 Key / Kind 解析工站 Id，看板与设备报警页共用。</summary>
public static class LineAlarmStation
{
    public static int IdOf(LineAlarm alarm) => IdOf(alarm.Key, alarm.Kind);

    public static int IdOf(string key, LineAlarmKind kind)
    {
        if (kind == LineAlarmKind.CustomDevice)
        {
            return StationCustomAlarmRules.StationIdOf(key);
        }

        if (kind is not (LineAlarmKind.ConsecutiveNg or LineAlarmKind.ConsecutiveWarning or LineAlarmKind.ProcessDrift
            or LineAlarmKind.StationFault or LineAlarmKind.ConsecutiveProcess))
        {
            return 0;
        }

        var parts = key.Split(':');
        return parts.Length >= 2 && int.TryParse(parts[1], out var id) && id > 0 ? id : 0;
    }

    /// <summary>按工站汇总当前板上的报警条数与首条短文案。</summary>
    public static IReadOnlyDictionary<int, StationOpenAlarmSummary> Summarize(IEnumerable<LineAlarm> alarms)
    {
        var map = new Dictionary<int, StationOpenAlarmSummary>();
        foreach (var alarm in alarms)
        {
            var stationId = IdOf(alarm);
            if (stationId <= 0)
            {
                continue;
            }

            if (map.TryGetValue(stationId, out var existing))
            {
                map[stationId] = existing with { Count = existing.Count + 1 };
                continue;
            }

            map[stationId] = new StationOpenAlarmSummary(stationId, 1, ShortHint(alarm.Message));
        }

        return map;
    }

    public static string ShortHint(string message, int maxLength = 28)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var text = message.Trim();
        // "工站 OP10 电机过热：..." → 去掉工站前缀，卡片上更短。
        if (text.StartsWith("工站 ", StringComparison.Ordinal))
        {
            var space = text.IndexOf(' ', 3);
            if (space > 0 && space + 1 < text.Length)
            {
                text = text[(space + 1)..].TrimStart();
            }
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 1)] + "…";
    }
}

/// <summary>某工站当前板上未解除（仍在呼叫）的报警摘要。</summary>
public readonly record struct StationOpenAlarmSummary(int StationId, int Count, string Hint);
