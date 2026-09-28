using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Application.Mes;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Tests;

public class TagWatchAndSessionTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 16, 0, 0);

    [Fact]
    public void Three_warnings_call_and_a_normal_piece_clears_them()
    {
        var streaks = new Dictionary<(int StationId, int TagId), TagWarningStreak>();
        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", true, false);
        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", true, false);
        Assert.Empty(Calling(streaks));

        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", true, false);
        var alarm = Assert.Single(Calling(streaks));
        Assert.Equal("warn:1:9", alarm.Key);
        Assert.Contains("压力", alarm.Message);

        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", false, false);
        Assert.Empty(Calling(streaks));
    }

    [Fact]
    public void Out_of_limit_clears_the_warning_streak()
    {
        var streaks = new Dictionary<(int StationId, int TagId), TagWarningStreak>();
        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", true, false);
        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", true, false);
        TagWatchRules.ApplyWarning(streaks, 1, "ST010", 9, "压力", false, true);

        Assert.Empty(streaks);
    }

    [Fact]
    public void A_spike_at_the_end_of_a_stable_run_is_process_drift()
    {
        var values = Enumerable.Repeat(10d, 20).Append(100d).ToList();
        var drift = TagWatchRules.DriftOf(2, "ST020", 4, "压力", values);

        Assert.NotNull(drift);
        Assert.Contains("超出控制限", drift.Value.Detail);
        var alarm = Assert.Single(LineAlarmRules.Evaluate(
            true, Now, false, 0, null, [], Now, drifts: [drift.Value]).Alarms);
        Assert.Equal("drift:2:4", alarm.Key);
    }

    [Fact]
    public void Eight_stable_points_do_not_drift()
    {
        Assert.Null(TagWatchRules.DriftOf(1, "ST010", 1, "温度", Enumerable.Repeat(10d, 8).ToList()));
    }

    [Fact]
    public void Open_session_calls_only_after_the_warn_window()
    {
        var sessions = new[]
        {
            new ActiveSessionIndex
            {
                SessionId = 1,
                PalletCode = "P1",
                SerialNo = "S1",
                StartTime = Now.AddMinutes(-10),
                LastStationCode = "ST010"
            },
            new ActiveSessionIndex
            {
                SessionId = 4,
                PalletCode = "P2",
                SerialNo = "S2",
                StartTime = Now.AddMinutes(-SystemDefaults.OpenSessionWarnMinutes),
                LastStationCode = "ST020"
            }
        };

        var due = OpenSessionRules.Due(sessions, Now);
        var notice = Assert.Single(due);
        Assert.Equal("P2", notice.PalletCode);
        Assert.Equal("ST020", notice.StationCode);

        var alarm = Assert.Single(LineAlarmRules.Evaluate(
            true, Now, false, 0, null, [], Now, openSessions: due).Alarms);
        Assert.Equal("open:4", alarm.Key);
        Assert.Contains("S2", alarm.Message);
        Assert.Contains("ST020", alarm.Message);
    }

    [Fact]
    public void Later_station_uses_the_frozen_recipe_not_the_one_just_activated()
    {
        var oldRecipe = new Recipe { Id = 1, Code = "A100", Name = "旧" };
        var newRecipe = new Recipe { Id = 2, Code = "B200", Name = "新" };
        var config = new AppConfigurationSnapshot
        {
            ActiveRecipe = newRecipe,
            Recipes = [oldRecipe, newRecipe]
        };

        Assert.Same(newRecipe, SessionRecipe.Select(config, firstStation: true, "A100"));
        Assert.Same(oldRecipe, SessionRecipe.Select(config, firstStation: false, "A100"));
        Assert.Equal("A100", SessionRecipe.RecordCode(false, "A100", newRecipe));
        Assert.Null(SessionRecipe.Select(config, firstStation: false, "GONE"));
        Assert.Equal("GONE", SessionRecipe.RecordCode(false, "GONE", newRecipe));
    }

    [Fact]
    public void Mes_payload_names_the_failed_point_and_the_limit_then_in_force()
    {
        var record = new CollectRecord
        {
            StationCode = "ST020",
            TagValues =
            [
                new TagValue
                {
                    TagName = "压力",
                    NumericValue = 19,
                    IsOutOfLimit = true,
                    UpperLimit = 16
                }
            ],
            Products = [new ProductRecord { Judgement = Judgement.Ng, NgReason = "压力超限" }]
        };
        var curve = new CollectRecord
        {
            StationCode = "ST030",
            Products = [new ProductRecord { Judgement = Judgement.Ng, NgReason = "波形超出包络" }]
        };

        var json = MesPayload.Build(
            "20260928-000001",
            "P1",
            "ST060",
            "A100",
            "Ng",
            Now,
            MesPayload.FromRecords([record, curve]));

        Assert.Contains("压力", json);
        Assert.Contains("A100", json);
        Assert.Contains("16", json);
        Assert.Contains("波形超出包络", json);
        Assert.DoesNotContain("压力超限", json);
    }

    private static IReadOnlyList<LineAlarm> Calling(IReadOnlyDictionary<(int StationId, int TagId), TagWarningStreak> streaks)
        => LineAlarmRules.Evaluate(true, Now, false, 0, null, [], Now, warningStreaks: streaks.Values.ToList()).Alarms;
}
