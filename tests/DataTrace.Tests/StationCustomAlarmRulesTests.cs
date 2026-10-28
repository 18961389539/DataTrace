using DataTrace.Application.Alarms;
using DataTrace.Application.Realtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Tests;

public class StationCustomAlarmRulesTests
{
    [Fact]
    public void StationIdOf_parses_custom_key()
    {
        Assert.Equal(12, StationCustomAlarmRules.StationIdOf("custom:12:3"));
        Assert.Equal(0, StationCustomAlarmRules.StationIdOf("fault:12"));
        Assert.Equal(0, StationCustomAlarmRules.StationIdOf("custom:x:1"));
    }

    [Fact]
    public void Evaluate_emits_alarm_when_last_tag_breaches_limit()
    {
        var station = new Station
        {
            Id = 5,
            Code = "OP10",
            Sequence = 10,
            Tags = { new TagDefinition { Id = 9, StationId = 5, Name = "电机温度" } },
            Alarms =
            {
                new StationAlarmDefinition
                {
                    Id = 3,
                    StationId = 5,
                    Name = "电机过热",
                    Enabled = true,
                    TagDefinitionId = 9,
                    Compare = StationAlarmCompare.Above,
                    Limit = 80
                }
            }
        };
        var status = new StationRuntimeStatus
        {
            StationId = 5,
            StationCode = "OP10",
            LastTags =
            [
                new StationLiveTag { Name = "电机温度", Display = "90", NumericValue = 90 }
            ]
        };

        var alarms = StationCustomAlarmRules.Evaluate([station], [status]);
        var alarm = Assert.Single(alarms);
        Assert.Equal("custom:5:3", alarm.Key);
        Assert.Equal(LineAlarmKind.CustomDevice, alarm.Kind);
        Assert.Contains("电机过热", alarm.Message);
        Assert.Contains("90", alarm.Message);
    }

    [Fact]
    public void Evaluate_skips_when_within_limit_or_disabled()
    {
        var station = new Station
        {
            Id = 5,
            Code = "OP10",
            Tags = { new TagDefinition { Id = 9, Name = "电机温度" } },
            Alarms =
            {
                new StationAlarmDefinition
                {
                    Id = 3, StationId = 5, Name = "电机过热", Enabled = true,
                    TagDefinitionId = 9, Compare = StationAlarmCompare.Above, Limit = 80
                },
                new StationAlarmDefinition
                {
                    Id = 4, StationId = 5, Name = "停用项", Enabled = false,
                    TagDefinitionId = 9, Compare = StationAlarmCompare.Above, Limit = 10
                }
            }
        };
        var status = new StationRuntimeStatus
        {
            StationId = 5,
            LastTags = [new StationLiveTag { Name = "电机温度", Display = "70", NumericValue = 70 }]
        };

        Assert.Empty(StationCustomAlarmRules.Evaluate([station], [status]));
    }
}