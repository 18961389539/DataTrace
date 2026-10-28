using DataTrace.Application.Realtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Alarms;

/// <summary>工站自定义设备报警：按最近一次采集点位值与配置限值比较。</summary>
public static class StationCustomAlarmRules
{
    public static string KeyOf(int stationId, int alarmId)
        => $"custom:{stationId}:{alarmId}";

    public static int StationIdOf(string key)
    {
        if (!key.StartsWith("custom:", StringComparison.Ordinal))
        {
            return 0;
        }

        var parts = key.Split(':');
        return parts.Length >= 2 && int.TryParse(parts[1], out var id) && id > 0 ? id : 0;
    }

    public static IReadOnlyList<LineAlarm> Evaluate(
        IEnumerable<Station> stations,
        IEnumerable<StationRuntimeStatus> statuses)
    {
        var byId = statuses.ToDictionary(s => s.StationId);
        var alarms = new List<LineAlarm>();

        foreach (var station in stations.OrderBy(s => s.Sequence).ThenBy(s => s.Code, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(station.Id, out var status))
            {
                continue;
            }

            var code = string.IsNullOrWhiteSpace(station.Code) ? station.Id.ToString() : station.Code;
            foreach (var alarm in station.Alarms.Where(a => a.Enabled).OrderBy(a => a.Name, StringComparer.Ordinal))
            {
                if (alarm.TagDefinitionId is not int tagId || alarm.Limit is not double limit)
                {
                    continue;
                }

                var tag = station.Tags.FirstOrDefault(t => t.Id == tagId);
                if (tag is null)
                {
                    continue;
                }

                var live = status.LastTags.FirstOrDefault(t =>
                    string.Equals(t.Name, tag.Name, StringComparison.OrdinalIgnoreCase));
                if (live?.NumericValue is not double value)
                {
                    continue;
                }

                var breached = alarm.Compare == StationAlarmCompare.Above
                    ? value > limit
                    : value < limit;
                if (!breached)
                {
                    continue;
                }

                var compareText = alarm.Compare == StationAlarmCompare.Above ? "高于" : "低于";
                alarms.Add(new LineAlarm(
                    KeyOf(station.Id, alarm.Id),
                    LineAlarmKind.CustomDevice,
                    $"工站 {code} {alarm.Name}：当前 {value}，限值 {limit}（{compareText}）"));
            }
        }

        return alarms;
    }
}