using DataTrace.Application.Alarms;

namespace DataTrace.Tests;

public class LineAlarmStationTests
{
    [Fact]
    public void IdOf_parses_station_scoped_keys()
    {
        Assert.Equal(12, LineAlarmStation.IdOf("custom:12:3", LineAlarmKind.CustomDevice));
        Assert.Equal(7, LineAlarmStation.IdOf("fault:7", LineAlarmKind.StationFault));
        Assert.Equal(7, LineAlarmStation.IdOf("ng:7", LineAlarmKind.ConsecutiveNg));
        Assert.Equal(7, LineAlarmStation.IdOf("proc:7", LineAlarmKind.ConsecutiveProcess));
        Assert.Equal(7, LineAlarmStation.IdOf("warn:7:9", LineAlarmKind.ConsecutiveWarning));
        Assert.Equal(7, LineAlarmStation.IdOf("drift:7:9", LineAlarmKind.ProcessDrift));
        Assert.Equal(0, LineAlarmStation.IdOf("heartbeat", LineAlarmKind.HeartbeatStale));
        Assert.Equal(0, LineAlarmStation.IdOf("collection-age:OP10", LineAlarmKind.CollectionOverdue));
    }

    [Fact]
    public void Summarize_groups_by_station_and_keeps_first_hint()
    {
        var map = LineAlarmStation.Summarize(
        [
            new LineAlarm("custom:5:1", LineAlarmKind.CustomDevice, "工站 OP10 电机过热：当前 90，限值 80（高于）"),
            new LineAlarm("ng:5", LineAlarmKind.ConsecutiveNg, "工站 OP10 连续 3 件不合格。"),
            new LineAlarm("heartbeat", LineAlarmKind.HeartbeatStale, "采集心跳已停止")
        ]);

        var summary = Assert.Single(map);
        Assert.Equal(5, summary.Key);
        Assert.Equal(2, summary.Value.Count);
        Assert.Equal("电机过热：当前 90，限值 80（高于）", summary.Value.Hint);
    }

    [Fact]
    public void ShortHint_truncates_long_text()
    {
        var hint = LineAlarmStation.ShortHint(new string('测', 40), maxLength: 10);
        Assert.Equal(10, hint.Length);
        Assert.EndsWith("…", hint);
    }
}
