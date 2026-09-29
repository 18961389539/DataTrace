using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Reporting;

namespace DataTrace.Tests;

/// <summary>
/// 合格率按走出末站的一件算。一件在中间站判废、后面工站仍记 OK 时，只算一次不合格。
/// </summary>
public class PieceYieldRulesTests
{
    private static readonly DateTime Day = new(2026, 9, 19, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void A_piece_that_failed_midway_counts_once_even_when_later_stations_are_ok()
    {
        var scrap = Piece(1, Day, Judgement.Ng,
            stations:
            [
                new PieceStationMark(10, "ST010", Judgement.Ok),
                new PieceStationMark(20, "ST020", Judgement.Ng),
                new PieceStationMark(40, "ST040", Judgement.Ok)
            ],
            faults: [new PieceFaultPoint(20, "ST020", "压力")]);
        var good = Piece(2, Day.AddMinutes(5), Judgement.Ok,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ok), new PieceStationMark(40, "ST040", Judgement.Ok)]);

        var report = PieceYieldRules.Summarize([scrap, good], stationId: null, 8, 12);

        var row = Assert.Single(report.ByShift);
        Assert.Equal(2, row.Total);
        Assert.Equal(1, row.Ok);
        Assert.Equal(1, row.Ng);
        Assert.Equal(0.5, row.FirstPassYield);
        Assert.Equal("ST020", report.Drag!.StationCode);
        Assert.Equal("压力", report.Drag.PointName);
        Assert.Equal(1, report.Drag.PieceCount);
        Assert.Equal("不合格多出在 ST020 压力，1 件", report.Drag.Text);
    }

    [Fact]
    public void Quality_rejects_and_collection_failures_are_counted_apart()
    {
        var quality = Piece(1, Day, Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng, ResultCodes.QualityRejected)],
            faults: [new PieceFaultPoint(20, "ST020", "压力")]);
        var missed = Piece(2, Day.AddMinutes(1), Judgement.Ng,
            stations:
            [
                new PieceStationMark(10, "ST010", Judgement.Ng, ResultCodes.DataValidationFailed),
                new PieceStationMark(40, "ST040", Judgement.Ok, ResultCodes.Success)
            ]);
        var both = Piece(3, Day.AddMinutes(2), Judgement.Ng,
            stations:
            [
                new PieceStationMark(10, "ST010", Judgement.Ng, ResultCodes.ProcessAbnormal),
                new PieceStationMark(20, "ST020", Judgement.Ng, ResultCodes.QualityRejected)
            ],
            faults: [new PieceFaultPoint(20, "ST020", "温度")]);

        var report = PieceYieldRules.Summarize([quality, missed, both], null, 8, 12);
        var row = Assert.Single(report.ByShift);
        Assert.Equal(3, row.Ng);
        Assert.Equal(2, row.QualityNg);
        Assert.Equal(1, row.CollectNg);
        Assert.Equal("质量不合格 2 件 · 没采成 1 件", PieceYieldRules.NgSourceText(row.QualityNg, row.CollectNg));
        Assert.Equal("压力", report.Drag!.PointName);
        Assert.Equal(1, report.Drag.PieceCount);

        var atFirst = PieceYieldRules.Summarize([missed, both], 10, 8, 12);
        var first = Assert.Single(atFirst.ByShift);
        Assert.Equal(2, first.Ng);
        Assert.Equal(0, first.QualityNg);
        Assert.Equal(2, first.CollectNg);
        Assert.Null(atFirst.Drag);

        var atPress = PieceYieldRules.Summarize([both], 20, 8, 12);
        Assert.Equal(1, Assert.Single(atPress.ByShift).QualityNg);
    }

    [Fact]
    public void Undecided_pieces_stay_out_of_the_yield_and_a_clean_shift_says_nothing_was_scrap()
    {
        var none = Piece(1, Day, Judgement.None, stations: [new PieceStationMark(10, "ST010", Judgement.None)]);
        var report = PieceYieldRules.Summarize([none], null, 8, 12);

        var row = Assert.Single(report.ByShift);
        Assert.Equal(1, row.Total);
        Assert.Equal(1, row.None);
        Assert.Equal(0, row.FirstPassYield);
        Assert.Null(report.Drag);
    }

    [Fact]
    public void Night_shift_keeps_a_piece_that_finishes_after_midnight()
    {
        var night = Piece(1, new DateTime(2026, 9, 20, 2, 0, 0), Judgement.Ok,
            stations: [new PieceStationMark(40, "ST040", Judgement.Ok)]);
        var evening = Piece(2, new DateTime(2026, 9, 19, 21, 0, 0), Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults: [new PieceFaultPoint(20, "ST020", "压力")]);
        var morning = Piece(3, new DateTime(2026, 9, 20, 8, 0, 0), Judgement.Ok,
            stations: [new PieceStationMark(40, "ST040", Judgement.Ok)]);

        var report = PieceYieldRules.Summarize([night, evening, morning], null, 8, 12);

        Assert.Equal(2, report.ByShift.Count);
        var first = report.ByShift[0];
        Assert.Equal(new DateTime(2026, 9, 19, 20, 0, 0), first.Day);
        Assert.Equal("09-19 20:00–09-20 08:00", first.ShiftLabel);
        Assert.Equal(2, first.Total);
        Assert.Equal(1, first.Ng);
        Assert.Equal("09-20 08:00–20:00", report.ByShift[1].ShiftLabel);
        Assert.Equal(1, report.ByShift[1].Ok);
    }

    [Fact]
    public void Drag_counts_each_point_once_per_piece_and_keeps_the_busiest()
    {
        var both = Piece(1, Day, Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults:
            [
                new PieceFaultPoint(20, "ST020", "压力"),
                new PieceFaultPoint(20, "ST020", "压力"),
                new PieceFaultPoint(20, "ST020", "温度")
            ]);
        var pressure = Piece(2, Day.AddMinutes(1), Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults: [new PieceFaultPoint(20, "ST020", "压力")]);
        var other = Piece(3, Day.AddMinutes(2), Judgement.Ng,
            stations: [new PieceStationMark(50, "ST050", Judgement.Ng)],
            faults: [new PieceFaultPoint(50, "ST050", "温度")]);

        var report = PieceYieldRules.Summarize([both, pressure, other], null, 8, 12);

        Assert.Equal("ST020", report.Drag!.StationCode);
        Assert.Equal("压力", report.Drag.PointName);
        Assert.Equal(2, report.Drag.PieceCount);
    }

    [Fact]
    public void A_scrap_without_a_named_point_still_names_the_station()
    {
        var scrap = Piece(1, Day, Judgement.Ng, stations: [new PieceStationMark(20, "ST020", Judgement.Ng)]);
        var report = PieceYieldRules.Summarize([scrap], null, 8, 12);

        Assert.Equal("不合格", report.Drag!.PointName);
        Assert.Equal("不合格多出在 ST020，1 件", report.Drag.Text);
    }

    [Fact]
    public void Station_filter_uses_that_stations_own_judgement()
    {
        var piece = Piece(1, Day, Judgement.Ng,
            stations:
            [
                new PieceStationMark(20, "ST020", Judgement.Ng),
                new PieceStationMark(40, "ST040", Judgement.Ok)
            ],
            faults:
            [
                new PieceFaultPoint(20, "ST020", "压力"),
                new PieceFaultPoint(40, "ST040", "温度")
            ]);
        var missed = Piece(2, Day, Judgement.Ok, stations: [new PieceStationMark(10, "ST010", Judgement.Ok)]);

        var atLast = PieceYieldRules.Summarize([piece, missed], 40, 8, 12);
        var row = Assert.Single(atLast.ByShift);
        Assert.Equal(1, row.Total);
        Assert.Equal(1, row.Ok);
        Assert.Equal(0, row.Ng);
        Assert.Null(atLast.Drag);

        var atScrap = PieceYieldRules.Summarize([piece, missed], 20, 8, 12);
        Assert.Equal(1, Assert.Single(atScrap.ByShift).Ng);
        Assert.Equal("压力", atScrap.Drag!.PointName);

        Assert.Empty(PieceYieldRules.Summarize([piece], 99, 8, 12).ByShift);
    }

    [Fact]
    public void First_ng_station_follows_process_order_not_station_id()
    {
        var laterIdEarlierStep = Piece(1, Day, Judgement.Ng,
            stations:
            [
                new PieceStationMark(30, "ST030", Judgement.Ng, Sequence: 1),
                new PieceStationMark(10, "ST010", Judgement.Ng, Sequence: 2)
            ]);

        var report = PieceYieldRules.Summarize([laterIdEarlierStep], null, 8, 12);

        Assert.Equal("最先坏在 ST030，1 件", report.FirstNg!.Text);
    }

    [Fact]
    public void First_ng_tie_prefers_the_earlier_step_then_the_most_pieces()
    {
        var early = Piece(1, Day, Judgement.Ng,
            stations: [new PieceStationMark(30, "ST030", Judgement.Ng, Sequence: 1)]);
        var late = Piece(2, Day.AddMinutes(1), Judgement.Ng,
            stations: [new PieceStationMark(10, "ST010", Judgement.Ng, Sequence: 5)]);

        var tied = PieceYieldRules.Summarize([early, late], null, 8, 12);
        Assert.Equal("ST030", tied.FirstNg!.StationCode);

        var anotherLate = Piece(3, Day.AddMinutes(2), Judgement.Ng,
            stations: [new PieceStationMark(10, "ST010", Judgement.Ng, Sequence: 5)]);
        var majority = PieceYieldRules.Summarize([early, late, anotherLate], null, 8, 12);
        Assert.Equal("最先坏在 ST010，2 件", majority.FirstNg!.Text);
    }

    [Fact]
    public void First_ng_without_sequence_orders_by_station_id()
    {
        var piece = Piece(1, Day, Judgement.Ng,
            stations:
            [
                new PieceStationMark(50, "ST050", Judgement.Ng),
                new PieceStationMark(10, "ST010", Judgement.Ng)
            ]);

        var report = PieceYieldRules.Summarize([piece], null, 8, 12);

        Assert.Equal("ST010", report.FirstNg!.StationCode);
    }

    [Fact]
    public void Station_filter_still_names_the_earlier_ng_station_on_the_same_piece()
    {
        var piece = Piece(1, Day, Judgement.Ng,
            stations:
            [
                new PieceStationMark(10, "ST010", Judgement.Ng, Sequence: 1),
                new PieceStationMark(50, "ST050", Judgement.Ng, Sequence: 2)
            ],
            faults: [new PieceFaultPoint(50, "ST050", "压力", PointSide.High)]);

        var report = PieceYieldRules.Summarize([piece], 50, 8, 12);

        Assert.Equal("压力", report.Drag!.PointName);
        Assert.Equal("ST010", report.FirstNg!.StationCode);
    }

    [Fact]
    public void Drag_point_counts_high_and_low_once_per_piece_and_skips_missing_values()
    {
        var high = Piece(1, Day, Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults:
            [
                new PieceFaultPoint(20, "ST020", "压力", PointSide.High),
                new PieceFaultPoint(20, "ST020", "压力", PointSide.High)
            ]);
        var both = Piece(2, Day.AddMinutes(1), Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults:
            [
                new PieceFaultPoint(20, "ST020", "压力", PointSide.Low),
                new PieceFaultPoint(20, "ST020", "压力", PointSide.Missing)
            ]);
        var onlyMissing = Piece(3, Day.AddMinutes(2), Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults: [new PieceFaultPoint(20, "ST020", "压力", PointSide.Missing)]);

        var report = PieceYieldRules.Summarize([high, both, onlyMissing], null, 8, 12);

        Assert.Equal("压力", report.Drag!.PointName);
        Assert.Equal(2, report.Drag.PieceCount);
        Assert.Equal(1, report.Drag.HighCount);
        Assert.Equal(1, report.Drag.LowCount);
        Assert.Equal("偏高 1 件 · 偏低 1 件", report.Drag.SideText);
        Assert.Equal("不合格多出在 ST020 压力，2 件", report.Drag.Text);
    }

    [Fact]
    public void A_quality_piece_with_only_missing_points_falls_back_to_the_station()
    {
        var piece = Piece(1, Day, Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults: [new PieceFaultPoint(20, "ST020", "压力", PointSide.Missing)]);

        var report = PieceYieldRules.Summarize([piece], null, 8, 12);

        Assert.Equal("不合格", report.Drag!.PointName);
        Assert.Equal("", report.Drag.SideText);
    }

    [Fact]
    public async Task Recipes_are_grouped_and_the_service_uses_the_shift_window()
    {
        var store = new FakeRuntimeStore();
        store.FinishedPieces.Add(Piece(1, Day, Judgement.Ok, "A100", [new PieceStationMark(10, "ST010", Judgement.Ok)]));
        store.FinishedPieces.Add(Piece(2, Day.AddMinutes(1), Judgement.Ng, "B200",
            [new PieceStationMark(20, "ST020", Judgement.Ng)],
            [new PieceFaultPoint(20, "ST020", "波形超出包络")]));
        store.FinishedPieces.Add(Piece(3, Day.AddDays(-2), Judgement.Ok, "A100", [new PieceStationMark(10, "ST010", Judgement.Ok)]));

        var service = new ReportService(store);
        var report = await service.GetPieceYieldAsync(Day.Date, Day.Date.AddDays(1).AddTicks(-1), null);

        Assert.Equal(2, report.ByRecipe.Count);
        Assert.Equal("A100", report.ByRecipe[0].RecipeCode);
        Assert.Equal(1, report.ByRecipe[0].Ok);
        Assert.Equal(1, report.ByRecipe[1].Ng);
        Assert.Equal("波形超出包络", report.Drag!.PointName);
        Assert.Equal("09-19 08:00–20:00", Assert.Single(report.ByShift).ShiftLabel);

        var onlyB = await service.GetPieceYieldAsync(Day.Date, Day.Date.AddDays(1), null, "B200");
        Assert.Equal("B200", Assert.Single(onlyB.ByRecipe).RecipeCode);
    }

    [Fact]
    public async Task The_service_measures_clearance_only_for_the_drag_point()
    {
        var store = new FakeRuntimeStore();
        store.FinishedPieces.Add(Piece(1, Day, Judgement.Ng,
            stations: [new PieceStationMark(20, "ST020", Judgement.Ng)],
            faults: [new PieceFaultPoint(20, "ST020", "压力", PointSide.High)]));
        store.InSpecReadings.Add(new InSpecReading(18, 0, 20));

        var report = await new ReportService(store).GetPieceYieldAsync(Day.Date, Day.Date.AddDays(1), null);

        Assert.Equal("偏高 1 件", report.Drag!.SideText);
        Assert.Equal("合格件里最近离红线还有 2，1 件落在靠红线的一成内", report.Clearance!.Text);
    }

    private static FinishedPieceObservation Piece(
        long id,
        DateTime end,
        Judgement judgement,
        string recipe = "",
        IReadOnlyList<PieceStationMark>? stations = null,
        IReadOnlyList<PieceFaultPoint>? faults = null)
        => new()
        {
            MonthKey = "202609",
            SessionId = id,
            EndTime = end,
            Judgement = judgement,
            RecipeCode = recipe,
            Stations = stations ?? [],
            Faults = faults ?? []
        };
}
