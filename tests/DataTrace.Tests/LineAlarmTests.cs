using DataTrace.Application.Alarms;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Realtime;

namespace DataTrace.Tests;

public class LineAlarmTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 15, 0, 0);

    [Fact]
    public void Fresh_collector_does_not_call()
    {
        var snapshot = LineAlarmRules.Evaluate(
            collectEnabled: true,
            lastCollectorAt: Now.AddSeconds(-5),
            mesEnabled: false,
            mesPendingCount: 0,
            mesOldestPendingAt: null,
            ngStreaks: [],
            Now);

        Assert.Empty(snapshot.Alarms);
    }

    [Fact]
    public void Stale_heartbeat_calls_only_while_collection_is_enabled()
    {
        var stale = Now.AddSeconds(-SystemDefaults.StaleHeartbeatSeconds - 1);
        var calling = LineAlarmRules.Evaluate(true, stale, false, 0, null, [], Now);
        var quiet = LineAlarmRules.Evaluate(false, stale, false, 0, null, [], Now);

        Assert.Equal("heartbeat", Assert.Single(calling.Alarms).Key);
        Assert.Empty(quiet.Alarms);
    }

    [Fact]
    public void Mes_backlog_calls_after_the_warn_window()
    {
        var old = Now.AddHours(-SystemDefaults.MesBacklogWarnHours).AddMinutes(-1);
        var calling = LineAlarmRules.Evaluate(true, Now, true, 4, old, [], Now);
        var recent = LineAlarmRules.Evaluate(true, Now, true, 4, Now.AddMinutes(-10), [], Now);
        var disabled = LineAlarmRules.Evaluate(true, Now, false, 4, old, [], Now);

        Assert.Contains("4", Assert.Single(calling.Alarms).Message);
        Assert.Empty(recent.Alarms);
        Assert.Empty(disabled.Alarms);
    }

    [Fact]
    public void Consecutive_ng_calls_at_the_threshold_and_orders_by_station()
    {
        var streaks = new StationNgStreak[]
        {
            new(2, "ST020", SystemDefaults.ConsecutiveNgAlarmCount),
            new(1, "ST010", SystemDefaults.ConsecutiveNgAlarmCount - 1)
        };

        var snapshot = LineAlarmRules.Evaluate(true, Now, false, 0, null, streaks, Now);

        var alarm = Assert.Single(snapshot.Alarms);
        Assert.Equal("ng:2", alarm.Key);
        Assert.Contains("ST020", alarm.Message);
    }

    [Fact]
    public void Due_sends_new_alarms_immediately_and_repeats_after_the_interval()
    {
        var alarm = new LineAlarm("heartbeat", LineAlarmKind.HeartbeatStale, "停了");
        var sent = new Dictionary<string, DateTime> { ["heartbeat"] = Now.AddMinutes(-SystemDefaults.AlarmRepeatMinutes + 1) };

        Assert.Empty(LineAlarmRules.Due([alarm], sent, Now, TimeSpan.FromMinutes(SystemDefaults.AlarmRepeatMinutes)));

        sent["heartbeat"] = Now.AddMinutes(-SystemDefaults.AlarmRepeatMinutes);
        Assert.Equal("heartbeat", Assert.Single(LineAlarmRules.Due([alarm], sent, Now, TimeSpan.FromMinutes(SystemDefaults.AlarmRepeatMinutes))).Key);
        Assert.Equal("mes", Assert.Single(LineAlarmRules.Due(
            [alarm, new LineAlarm("mes", LineAlarmKind.MesBacklog, "积压")],
            sent,
            Now,
            TimeSpan.FromMinutes(SystemDefaults.AlarmRepeatMinutes)).Where(a => a.Key == "mes")).Key);
    }

    [Fact]
    public void Ok_clears_a_station_streak_and_none_leaves_it()
    {
        var hub = new RuntimeStatusHub();
        Publish(hub, 7, "ST010", Judgement.Ng);
        Publish(hub, 7, "ST010", Judgement.Ng);
        Publish(hub, 7, "ST010", Judgement.None);

        Assert.Equal(2, Assert.Single(hub.NgStreaks).Count);

        Publish(hub, 7, "ST010", Judgement.Ok);

        Assert.Empty(hub.NgStreaks);
    }

    [Fact]
    public void Quality_and_process_streaks_call_separately()
    {
        var streaks = new StationNgStreak[]
        {
            new(1, "ST010", SystemDefaults.ConsecutiveNgAlarmCount, StationStreakKind.Quality),
            new(1, "ST010", SystemDefaults.ConsecutiveNgAlarmCount, StationStreakKind.Process)
        };

        var snapshot = LineAlarmRules.Evaluate(true, Now, false, 0, null, streaks, Now);
        var keys = snapshot.Alarms.Select(alarm => alarm.Key).ToArray();

        Assert.Equal(new[] { "ng:1", "proc:1" }, keys);
        Assert.Contains("质量不合格", snapshot.Alarms[0].Message);
        Assert.Contains("采集失败或跳站", snapshot.Alarms[1].Message);
    }

    [Fact]
    public void Station_fault_calls_while_collection_is_enabled()
    {
        var faults = new StationFaultNotice[] { new(3, "ST030", "PLC 读取失败") };
        var calling = LineAlarmRules.Evaluate(true, Now, false, 0, null, [], Now, faults);
        var quiet = LineAlarmRules.Evaluate(false, Now, false, 0, null, [], Now, faults);

        var alarm = Assert.Single(calling.Alarms);
        Assert.Equal("fault:3", alarm.Key);
        Assert.Contains("ST030", alarm.Message);
        Assert.Contains("PLC 读取失败", alarm.Message);
        Assert.Empty(quiet.Alarms);
    }

    [Fact]
    public void Spool_calls_only_after_the_warn_window()
    {
        var old = Now.AddMinutes(-SystemDefaults.SpoolBacklogWarnMinutes);
        var calling = LineAlarmRules.Evaluate(true, Now, false, 0, null, [], Now, spoolPendingCount: 2, spoolOldestAt: old);
        var recent = LineAlarmRules.Evaluate(true, Now, false, 0, null, [], Now, spoolPendingCount: 2, spoolOldestAt: Now.AddMinutes(-1));

        Assert.Contains("2", Assert.Single(calling.Alarms).Message);
        Assert.Equal("spool", calling.Alarms[0].Key);
        Assert.Empty(recent.Alarms);
    }

    [Fact]
    public void Process_failure_does_not_add_to_the_quality_streak()
    {
        var hub = new RuntimeStatusHub();
        Publish(hub, 7, "ST010", Judgement.Ng, ResultCodes.QualityRejected);
        Publish(hub, 7, "ST010", Judgement.Ng, ResultCodes.QualityRejected);
        Publish(hub, 7, "ST010", Judgement.Ng, ResultCodes.ProcessAbnormal);

        var streak = Assert.Single(hub.NgStreaks);
        Assert.Equal(StationStreakKind.Process, streak.Kind);
        Assert.Equal(1, streak.Count);
    }

    [Fact]
    public void Superseded_session_stays_open_until_someone_takes_it()
    {
        var open = new AlarmIncidentDraft
        {
            Id = 9,
            Key = LineAlarmKeys.SessionSuperseded(42),
            Kind = LineAlarmKind.SessionSuperseded,
            Message = "托盘 P1 被盖掉",
            RaisedAt = Now
        };

        var waiting = Assert.Single(AlarmIncidentRules.Merge([open], [], Now.AddMinutes(1)));
        Assert.Null(waiting.ClearedAt);
        Assert.True(waiting.NeedsOwner);

        var taken = open with { AcknowledgedAt = Now.AddMinutes(2), AcknowledgedBy = "admin" };
        var closed = Assert.Single(AlarmIncidentRules.Merge([taken], [], Now.AddMinutes(3)));
        Assert.Equal(Now.AddMinutes(3), closed.ClearedAt);
        Assert.False(closed.NeedsOwner);
    }

    [Fact]
    public void Merge_opens_one_incident_and_keeps_it_until_someone_takes_it()
    {
        var now = Now;
        var first = AlarmIncidentRules.Merge(
            [],
            [new LineAlarm("heartbeat", LineAlarmKind.HeartbeatStale, "停了")],
            now);

        var opened = Assert.Single(first);
        Assert.Equal(0, opened.Id);
        Assert.True(opened.NeedsOwner);
        Assert.True(opened.StillActive);

        var cleared = AlarmIncidentRules.Merge([], [], now.AddMinutes(2));
        Assert.Empty(cleared);

        var waiting = AlarmIncidentRules.Merge(first, [], now.AddMinutes(2));
        var parked = Assert.Single(waiting);
        Assert.Equal(now.AddMinutes(2), parked.ClearedAt);
        Assert.True(parked.NeedsOwner);

        var again = AlarmIncidentRules.Merge(
            waiting,
            [new LineAlarm("heartbeat", LineAlarmKind.HeartbeatStale, "还是停")],
            now.AddMinutes(3));
        var reopened = Assert.Single(again);
        Assert.Null(reopened.ClearedAt);
        Assert.Equal("还是停", reopened.Message);
        Assert.Equal(now, reopened.RaisedAt);
    }

    [Fact]
    public void Merge_keeps_the_owner_while_the_condition_continues()
    {
        var owned = new AlarmIncidentDraft
        {
            Id = 4,
            Key = "ng:2",
            Kind = LineAlarmKind.ConsecutiveNg,
            Message = "连续 3 件",
            RaisedAt = Now.AddMinutes(-10),
            AcknowledgedAt = Now.AddMinutes(-5),
            AcknowledgedBy = "zhang"
        };

        var next = AlarmIncidentRules.Merge(
            [owned],
            [new LineAlarm("ng:2", LineAlarmKind.ConsecutiveNg, "连续 4 件")],
            Now);

        var row = Assert.Single(next);
        Assert.Equal(4, row.Id);
        Assert.Equal("zhang", row.AcknowledgedBy);
        Assert.Equal("连续 4 件", row.Message);
        Assert.False(row.NeedsOwner);
        Assert.True(row.StillActive);
    }

    [Fact]
    public void Merge_opens_a_fresh_row_when_nothing_open_remains()
    {
        var next = AlarmIncidentRules.Merge(
            [],
            [new LineAlarm("ng:2", LineAlarmKind.ConsecutiveNg, "又连续了")],
            Now);

        var opened = Assert.Single(next);
        Assert.Equal(0, opened.Id);
        Assert.Equal("又连续了", opened.Message);
        Assert.True(opened.NeedsOwner);
    }

    private static void Publish(
        RuntimeStatusHub hub,
        int stationId,
        string code,
        Judgement judgement,
        short resultCode = ResultCodes.QualityRejected)
        => hub.Publish(new CollectRecord
        {
            StationId = stationId,
            StationCode = code,
            Judgement = judgement,
            ResultCode = resultCode,
            TriggerTime = Now
        });
}
