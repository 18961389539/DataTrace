using DataTrace.Application.Reporting;
using DataTrace.Infrastructure.Reporting;

namespace DataTrace.Tests;

/// <summary>
/// 少做的件停在哪一站、停了多久。短于一分钟的间隔不算；整线一起停时记在当时件停住的工站。
/// </summary>
public class StationStopRulesTests
{
    private static readonly DateTime ShiftStart = new(2026, 9, 19, 8, 0, 0);

    [Fact]
    public void A_station_that_stops_while_upstream_keeps_running_is_the_stop()
    {
        var passes = new List<StationPass>();
        var end = ShiftStart.AddMinutes(20);
        Every(passes, 10, "ST010", 1, true, ShiftStart, end, TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart, ShiftStart.AddMinutes(2), TimeSpan.FromSeconds(10));
        var resume = ShiftStart.AddMinutes(2).AddSeconds(610);
        Every(passes, 20, "ST020", 2, true, resume, end, TimeSpan.FromSeconds(10));

        var row = One(passes);

        Assert.Equal("ST020", row.StationCode);
        Assert.False(row.WholeLine);
        Assert.Equal(TimeSpan.FromMinutes(10), row.Duration);
        Assert.Equal(60, row.LostPieces);
        Assert.Equal("少做在 ST020，停了 10 分钟，约 60 件", row.Detail);
    }

    [Fact]
    public void A_later_station_waiting_on_the_same_gap_is_not_counted_again()
    {
        var passes = new List<StationPass>();
        var end = ShiftStart.AddMinutes(20);
        Every(passes, 10, "ST010", 1, true, ShiftStart, end, TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart, ShiftStart.AddSeconds(100), TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart.AddSeconds(710), end, TimeSpan.FromSeconds(10));
        Every(passes, 30, "ST030", 3, true, ShiftStart, ShiftStart.AddSeconds(130), TimeSpan.FromSeconds(10));
        Every(passes, 30, "ST030", 3, true, ShiftStart.AddSeconds(710), end, TimeSpan.FromSeconds(10));

        var row = One(passes);

        Assert.Equal("ST020", row.StationCode);
        Assert.Equal(1, row.StopCount);
    }

    [Fact]
    public void A_line_wide_pause_is_named_for_the_station_where_most_pieces_sat()
    {
        var passes = new List<StationPass>();
        var runningUntil = ShiftStart.AddMinutes(2);
        var resume = runningUntil.AddSeconds(610);
        Every(passes, 60, "ST060", 6, true, ShiftStart, runningUntil, TimeSpan.FromSeconds(10));
        Every(passes, 60, "ST060", 6, true, resume, resume.AddMinutes(2), TimeSpan.FromSeconds(10));
        passes.Add(Pass(30, "ST030", 3, false, ShiftStart.AddSeconds(30), 9001));
        passes.Add(Pass(30, "ST030", 3, false, ShiftStart.AddSeconds(40), 9002));
        passes.Add(Pass(30, "ST030", 3, false, ShiftStart.AddSeconds(50), 9003));
        passes.Add(Pass(20, "ST020", 2, false, ShiftStart.AddSeconds(35), 9004));

        var row = One(passes);

        Assert.Equal("ST030", row.StationCode);
        Assert.Equal(TimeSpan.FromMinutes(10), row.Duration);
        Assert.Equal(60, row.LostPieces);
        Assert.Equal(1, row.StopCount);
    }

    [Fact]
    public void A_pause_with_no_piece_in_process_is_the_whole_line()
    {
        var passes = new List<StationPass>();
        var runningUntil = ShiftStart.AddMinutes(2);
        var resume = runningUntil.AddSeconds(610);
        Every(passes, 60, "ST060", 6, true, ShiftStart, runningUntil, TimeSpan.FromSeconds(10));
        Every(passes, 60, "ST060", 6, true, resume, resume.AddMinutes(2), TimeSpan.FromSeconds(10));

        var row = One(passes);

        Assert.True(row.WholeLine);
        Assert.Null(row.StationCode);
        Assert.Equal("少做在整线，停了 10 分钟，约 60 件", row.Detail);
    }

    [Fact]
    public void A_gap_under_a_minute_is_not_a_stop()
    {
        var passes = new List<StationPass>();
        var end = ShiftStart.AddMinutes(5);
        Every(passes, 10, "ST010", 1, true, ShiftStart, end, TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart, ShiftStart.AddMinutes(1), TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart.AddMinutes(1).AddSeconds(45), end, TimeSpan.FromSeconds(10));

        var row = One(passes);

        Assert.Equal(0, row.StopCount);
        Assert.Equal("本班各站没有明显停顿", row.Detail);
        Assert.Equal("没有停顿", row.Brief);
    }

    [Fact]
    public void The_longest_stop_is_named_and_the_pieces_add_up()
    {
        var passes = new List<StationPass>();
        var end = ShiftStart.AddMinutes(30);
        Every(passes, 10, "ST010", 1, true, ShiftStart, end, TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart, ShiftStart.AddMinutes(2), TimeSpan.FromSeconds(10));
        var afterShort = ShiftStart.AddMinutes(2).AddSeconds(190);
        Every(passes, 20, "ST020", 2, true, afterShort, afterShort.AddMinutes(1), TimeSpan.FromSeconds(10));
        var afterLong = afterShort.AddMinutes(1).AddSeconds(610);
        Every(passes, 20, "ST020", 2, true, afterLong, end, TimeSpan.FromSeconds(10));

        var row = One(passes);

        Assert.Equal(2, row.StopCount);
        Assert.Equal(TimeSpan.FromMinutes(10), row.Duration);
        Assert.Equal(60, row.LostPieces);
        Assert.Equal(78, row.TotalLostPieces);
        Assert.Contains("少做最多在 ST020", row.Detail);
        Assert.Contains("合计少约 78 件", row.Detail);
    }

    [Fact]
    public void Only_the_part_inside_the_shift_counts()
    {
        var passes = new List<StationPass>();
        var prev = ShiftStart.AddMinutes(-10).AddSeconds(-10);
        var next = ShiftStart.AddMinutes(10);
        Every(passes, 10, "ST010", 1, true, prev.AddMinutes(-2), next.AddMinutes(2), TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, prev.AddMinutes(-2), prev, TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, next, next.AddMinutes(2), TimeSpan.FromSeconds(10));

        var row = One(passes);

        Assert.Equal("ST020", row.StationCode);
        Assert.Equal(TimeSpan.FromMinutes(10), row.Duration);
        Assert.Equal(60, row.LostPieces);
    }

    [Fact]
    public void A_pause_after_midnight_stays_in_the_night_shift()
    {
        var night = new DateTime(2026, 9, 20, 2, 0, 0);
        var passes = new List<StationPass>();
        Every(passes, 60, "ST060", 6, true, night.AddMinutes(-2), night, TimeSpan.FromSeconds(10));
        var resume = night.AddSeconds(610);
        Every(passes, 60, "ST060", 6, true, resume, resume.AddMinutes(2), TimeSpan.FromSeconds(10));

        var end = passes.Max(pass => pass.CompleteTime);
        var report = StationStopRules.Summarize(
            passes,
            new DateTime(2026, 9, 19, 20, 0, 0),
            end,
            end,
            8,
            12);

        var row = Assert.Single(report.ByShift, item => item.StopCount > 0);
        Assert.Equal(new DateTime(2026, 9, 19, 20, 0, 0), row.ShiftStart);
        Assert.True(row.WholeLine);
        Assert.Equal(TimeSpan.FromMinutes(10), row.Duration);
    }

    [Fact]
    public async Task The_service_uses_the_same_shift_and_ignores_time_after_the_range()
    {
        var passes = new List<StationPass>();
        var end = ShiftStart.AddMinutes(20);
        Every(passes, 10, "ST010", 1, true, ShiftStart, end, TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart, ShiftStart.AddMinutes(2), TimeSpan.FromSeconds(10));
        Every(passes, 20, "ST020", 2, true, ShiftStart.AddMinutes(2).AddSeconds(610), end, TimeSpan.FromSeconds(10));

        var store = new FakeRuntimeStore();
        store.StationPasses.AddRange(passes);
        var service = new ReportService(store);

        var report = await service.GetShiftStopsAsync(ShiftStart, end);

        var row = Assert.Single(report.ByShift, item => item.StopCount > 0);
        Assert.Equal(ShiftStart, row.ShiftStart);
        Assert.Equal("ST020", row.StationCode);
        Assert.Equal(60, row.LostPieces);
    }

    private static ShiftStopSummary One(List<StationPass> passes)
    {
        var end = passes.Max(pass => pass.CompleteTime);
        var report = StationStopRules.Summarize(passes, ShiftStart, end, end, 8, 12);
        return Assert.Single(report.ByShift, row => row.ShiftStart == ShiftStart);
    }

    private static void Every(
        List<StationPass> passes,
        int stationId,
        string code,
        int sequence,
        bool last,
        DateTime start,
        DateTime end,
        TimeSpan step)
    {
        for (var time = start; time <= end; time += step)
        {
            passes.Add(Pass(stationId, code, sequence, last, time, passes.Count + 1));
        }
    }

    private static StationPass Pass(int stationId, string code, int sequence, bool last, DateTime time, long session)
        => new()
        {
            MonthKey = "202609",
            SessionId = session,
            StationId = stationId,
            StationCode = code,
            Sequence = sequence,
            IsLastStation = last,
            CompleteTime = time
        };
}
