using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Tests;

public class SettingsEditRulesTests
{
    [Fact]
    public void Apply_patches_only_fields_this_session_changed()
    {
        var latest = new SystemSettings
        {
            ScanIntervalMs = 800,
            WriteRetryCount = 2,
            WriteRetryDelayMs = 50,
            RetentionYears = 5,
            AuditRetentionYears = 3,
            MesTimeoutSeconds = 10,
            CollectEnabled = true,
            MesEnabled = true,
            MesEndpoint = "http://other/mes",
            AlarmWebhookUrl = "http://other/hook"
        };
        var loaded = SettingsEdit.From(latest);
        loaded.MesEndpoint = "http://mine/mes";
        loaded.AlarmWebhookUrl = null;
        var edit = loaded.Clone();
        edit.ScanIntervalMs = 1000;

        var target = SettingsEditRules.Apply(latest, edit, loaded);

        Assert.Equal(1000, target.ScanIntervalMs);
        Assert.Equal("http://other/mes", target.MesEndpoint);
        Assert.Equal("http://other/hook", target.AlarmWebhookUrl);
        Assert.True(target.MesEnabled);
        Assert.Equal(5, target.RetentionYears);
    }

    [Fact]
    public void Apply_treats_blank_and_null_webhook_as_the_same_value()
    {
        var latest = new SystemSettings
        {
            ScanIntervalMs = 200,
            WriteRetryCount = 1,
            WriteRetryDelayMs = 20,
            RetentionYears = 3,
            AuditRetentionYears = 1,
            MesTimeoutSeconds = 5,
            AlarmWebhookUrl = "http://kept/hook"
        };
        var loaded = SettingsEdit.From(latest);
        loaded.AlarmWebhookUrl = null;
        var edit = loaded.Clone();
        edit.AlarmWebhookUrl = "   ";

        var target = SettingsEditRules.Apply(latest, edit, loaded);

        Assert.Equal("http://kept/hook", target.AlarmWebhookUrl);
    }
}

public class NgStreakRebuildTests
{
    [Fact]
    public void None_does_not_clear_and_ok_resets()
    {
        var marks = new[]
        {
            Mark(1, "OP10", Judgement.Ng, 1),
            Mark(1, "OP10", Judgement.Ng, 2),
            Mark(1, "OP10", Judgement.None, 3),
            Mark(2, "OP20", Judgement.Ng, 1),
            Mark(2, "OP20", Judgement.Ok, 2),
            Mark(1, "OP10", Judgement.Ng, 4)
        };

        var streaks = NgStreakRebuild.From(marks).ToDictionary(s => s.StationId);

        Assert.Equal(3, streaks[1].Count);
        Assert.Equal(StationStreakKind.Quality, streaks[1].Kind);
        Assert.Equal("OP10", streaks[1].StationCode);
        Assert.False(streaks.ContainsKey(2));
    }

    [Fact]
    public void Process_failure_replaces_the_quality_streak()
    {
        var marks = new[]
        {
            Mark(1, "OP10", Judgement.Ng, 1),
            Mark(1, "OP10", Judgement.Ng, 2),
            new StationJudgementMark
            {
                StationId = 1,
                StationCode = "OP10",
                Judgement = Judgement.Ng,
                ResultCode = ResultCodes.DataValidationFailed,
                CompleteTime = new DateTime(2026, 9, 28, 8, 0, 3)
            }
        };

        var streak = Assert.Single(NgStreakRebuild.From(marks));
        Assert.Equal(StationStreakKind.Process, streak.Kind);
        Assert.Equal(1, streak.Count);
    }

    private static StationJudgementMark Mark(int stationId, string code, Judgement judgement, int second)
        => new()
        {
            StationId = stationId,
            StationCode = code,
            Judgement = judgement,
            ResultCode = ResultCodes.QualityRejected,
            CompleteTime = new DateTime(2026, 9, 28, 8, 0, second)
        };
}
