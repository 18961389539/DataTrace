using DataTrace.Application.Runtime;

namespace DataTrace.Tests;

/// <summary>看板上的当前间隔是相邻两件的完成间隔，取最长的那一站。</summary>
public class StationPaceRulesTests
{
    [Fact]
    public void The_longest_gap_names_that_station()
    {
        var pace = StationPaceRules.Slowest(
        [
            new StationPaceRules.Sample("ST010", 10, true, TimeSpan.FromSeconds(4)),
            new StationPaceRules.Sample("ST020", 20, true, TimeSpan.FromSeconds(12.4)),
            new StationPaceRules.Sample("ST030", 30, true, TimeSpan.FromSeconds(8))
        ]);

        Assert.NotNull(pace);
        Assert.Equal("ST020", pace.Value.StationCode);
        Assert.Equal("12 秒", pace.Value.Text);
        Assert.Equal("最慢在 ST020", pace.Value.Hint);
    }

    [Fact]
    public void A_tie_keeps_the_earlier_station()
    {
        var pace = StationPaceRules.Slowest(
        [
            new StationPaceRules.Sample("ST030", 30, true, TimeSpan.FromSeconds(8)),
            new StationPaceRules.Sample("ST010", 10, true, TimeSpan.FromSeconds(8))
        ]);

        Assert.Equal("ST010", pace?.StationCode);
    }

    [Fact]
    public void Disabled_stations_and_a_single_completion_do_not_set_the_pace()
    {
        var pace = StationPaceRules.Slowest(
        [
            new StationPaceRules.Sample("ST010", 10, false, TimeSpan.FromMinutes(30)),
            new StationPaceRules.Sample("ST020", 20, true, null),
            new StationPaceRules.Sample("ST030", 30, true, TimeSpan.Zero)
        ]);

        Assert.Null(pace);
        Assert.Equal("—", StationPaceRules.Format(TimeSpan.Zero));
        Assert.Equal("800 ms", StationPaceRules.Format(TimeSpan.FromMilliseconds(800)));
        Assert.Equal("4.2 秒", StationPaceRules.Format(TimeSpan.FromSeconds(4.2)));
        Assert.Equal("1 分 30 秒", StationPaceRules.Format(TimeSpan.FromSeconds(90)));
        Assert.Equal("34 分钟", StationPaceRules.Format(TimeSpan.FromMinutes(34)));
    }
}
