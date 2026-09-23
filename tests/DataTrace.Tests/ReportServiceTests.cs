using DataTrace.Application.Reporting;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Reporting;

namespace DataTrace.Tests;

/// <summary>报表统计：按日产量、不良 Top、点位趋势。</summary>
public class ReportServiceTests
{
    private static readonly DateTime Day1 = new(2026, 9, 19, 8, 0, 0);
    private static readonly DateTime Day2 = new(2026, 9, 20, 8, 0, 0);

    private static CollectRecord Record(DateTime time, Judgement judgement, int stationId, params TagValue[] tags)
        => new()
        {
            TriggerTime = time,
            CompleteTime = time.AddMilliseconds(100),
            Judgement = judgement,
            StationId = stationId,
            StationCode = $"ST{stationId:000}",
            PalletCode = $"P{time:HHmmss}",
            SerialNo = $"S{time:HHmmss}",
            ResultCode = ResultCodes.Success,
            TagValues = tags
        };

    private static TagValue Tag(int tagId, string name, double? value, bool outOfLimit, string code = "")
        => new()
        {
            TagId = tagId,
            TagCode = string.IsNullOrEmpty(code) ? $"T{tagId}" : code,
            TagName = name,
            DataType = PlcDataType.Float,
            NumericValue = value,
            IsOutOfLimit = outOfLimit
        };

    private static FakeRuntimeStore BuildStore()
    {
        var store = new FakeRuntimeStore();
        store.Records.Add(("202609", Record(Day1, Judgement.Ok, 10, Tag(1, "压力", 12.5, false))));
        store.Records.Add(("202609", Record(Day1.AddHours(1), Judgement.Ok, 10, Tag(1, "压力", 13.0, false))));
        store.Records.Add(("202609", Record(Day1.AddHours(2), Judgement.Ng, 20, Tag(1, "压力", 25.0, true), Tag(2, "工站温度", 95.0, true))));
        store.Records.Add(("202609", Record(Day2, Judgement.Ok, 30, Tag(1, "压力", 12.0, false))));
        store.Records.Add(("202609", Record(Day2.AddHours(1), Judgement.Ng, 30, Tag(1, "压力", 26.0, true))));
        return store;
    }

    [Fact]
    public async Task Throughput_groups_by_day_and_counts_ok_ng()
    {
        var service = new ReportService(BuildStore());

        var rows = await service.GetThroughputAsync(Day1, Day2.AddDays(1), stationId: null);

        Assert.Equal(2, rows.Count);
        Assert.Equal(Day1.Date, rows[0].Day);
        Assert.Equal(3, rows[0].Total);
        Assert.Equal(2, rows[0].Ok);
        Assert.Equal(1, rows[0].Ng);
        Assert.Equal(2d / 3d, rows[0].FirstPassYield, precision: 6);

        Assert.Equal(Day2.Date, rows[1].Day);
        Assert.Equal(2, rows[1].Total);
        Assert.Equal(1, rows[1].Ok);
        Assert.Equal(0.5, rows[1].FirstPassYield);
    }

    [Fact]
    public async Task Throughput_filters_by_station()
    {
        var service = new ReportService(BuildStore());

        var rows = await service.GetThroughputAsync(Day1, Day2.AddDays(1), stationId: 10);

        var row = Assert.Single(rows);
        Assert.Equal(Day1.Date, row.Day);
        Assert.Equal(2, row.Total);
        Assert.Equal(2, row.Ok);
        Assert.Equal(0, row.Ng);
        Assert.Equal(1.0, row.FirstPassYield);
    }

    [Fact]
    public async Task Throughput_excludes_records_outside_range()
    {
        var service = new ReportService(BuildStore());

        var rows = await service.GetThroughputAsync(Day2, Day2.AddDays(1), stationId: null);

        Assert.Single(rows);
        Assert.Equal(Day2.Date, rows[0].Day);
        Assert.Equal(2, rows[0].Total);
    }

    [Fact]
    public async Task Throughput_returns_empty_for_range_without_data()
    {
        var service = new ReportService(BuildStore());

        Assert.Empty(await service.GetThroughputAsync(new DateTime(2025, 1, 1), new DateTime(2025, 1, 31), null));
    }

    [Fact]
    public void Throughput_yield_is_zero_when_no_records()
    {
        // 空数据下不能出现除零，UI 直接显示 0。
        var row = new DailyThroughput { Day = Day1.Date, Total = 0, Ok = 0, Ng = 0 };
        Assert.Equal(0d, row.FirstPassYield);
    }

    [Fact]
    public async Task Defect_top_counts_out_of_limit_tags_in_descending_order()
    {
        var service = new ReportService(BuildStore());

        var top = await service.GetDefectTopAsync(Day1, Day2.AddDays(1));

        Assert.Equal(2, top.Count);
        Assert.Equal("压力", top[0].Name);
        Assert.Equal(2, top[0].Count);
        Assert.Equal("工站温度", top[1].Name);
        Assert.Equal(1, top[1].Count);
    }

    [Fact]
    public async Task Defect_top_honors_take_and_ignores_in_limit_tags()
    {
        var service = new ReportService(BuildStore());

        var top = await service.GetDefectTopAsync(Day1, Day2.AddDays(1), take: 1);

        var item = Assert.Single(top);
        Assert.Equal("压力", item.Name);
        // 前两条记录压力均在限内，不应计入不良。
        Assert.Equal(2, item.Count);
    }

    [Fact]
    public async Task Defect_top_falls_back_to_tag_code_when_name_missing()
    {
        var store = new FakeRuntimeStore();
        store.Records.Add(("202609", Record(Day1, Judgement.Ng, 10, Tag(7, "", 99, true, code: "ST010_TEMP"))));
        var service = new ReportService(store);

        var item = Assert.Single(await service.GetDefectTopAsync(Day1, Day2));
        Assert.Equal("ST010_TEMP", item.Name);
    }

    [Fact]
    public async Task Trend_filters_by_tag_and_orders_by_time()
    {
        var service = new ReportService(BuildStore());
        var first = Day1;
        var second = Day2.AddHours(1);

        var trend = await service.GetTrendAsync(Day1, Day2.AddDays(1), tagId: 1);

        Assert.Equal(5, trend.Count);
        Assert.Equal(first, trend[0].Time);
        Assert.Equal(12.5, trend[0].Value);
        Assert.Equal(second, trend[^1].Time);
        Assert.Equal(26.0, trend[^1].Value);
        Assert.Equal($"P{second:HHmmss}", trend[^1].PalletCode);
        Assert.True(trend.Zip(trend.Skip(1)).All(p => p.First.Time <= p.Second.Time));
    }

    [Fact]
    public async Task Trend_skips_null_numeric_and_unknown_tags()
    {
        var store = new FakeRuntimeStore();
        store.Records.Add(("202609", Record(Day1, Judgement.Ok, 10, Tag(1, "压力", null, false), Tag(2, "文本", 5, false))));
        store.Records.Add(("202609", Record(Day1.AddHours(1), Judgement.Ok, 10, Tag(1, "压力", 11, false))));
        var service = new ReportService(store);

        var trend = await service.GetTrendAsync(Day1, Day2, tagId: 1);
        Assert.Single(trend);
        Assert.Equal(11, trend[0].Value);

        Assert.Empty(await service.GetTrendAsync(Day1, Day2, tagId: 999));
    }

    [Fact]
    public async Task Trend_respects_time_range()
    {
        var service = new ReportService(BuildStore());

        var trend = await service.GetTrendAsync(Day1, Day1.AddHours(3), tagId: 1);

        Assert.Equal(3, trend.Count);
        Assert.All(trend, p => Assert.True(p.Time <= Day1.AddHours(3)));
    }

    [Fact]
    public async Task Warning_top_counts_the_warning_band_separately_from_defects()
    {
        var store = new FakeRuntimeStore();
        var warned = Record(Day1, Judgement.Ok, 10, Tag(1, "压力", 19.5, false));
        warned.TagValues.Single().IsWarning = true;
        store.Records.Add(("202609", warned));
        store.Records.Add(("202609", Record(Day1.AddHours(1), Judgement.Ng, 10,
            Tag(1, "压力", 25, true), Tag(2, "工站温度", 95, true))));
        var service = new ReportService(store);

        var warnings = await service.GetWarningTopAsync(Day1, Day2);

        var item = Assert.Single(warnings);
        Assert.Equal("压力", item.Name);
        Assert.Equal(1, item.Count);

        // 不良统计只看超规格点位，两者互不串台。
        var defects = await service.GetDefectTopAsync(Day1, Day2);
        Assert.Equal(2, defects.Count);
        Assert.Equal("压力", defects[0].Name);
        Assert.Equal(1, defects[0].Count);
    }

    [Fact]
    public async Task Warning_top_honors_take_and_time_range()
    {
        var store = new FakeRuntimeStore();
        var first = Record(Day1, Judgement.Ok, 10, Tag(1, "压力", 19.5, false));
        first.TagValues.Single().IsWarning = true;
        var second = Record(Day1.AddHours(1), Judgement.Ok, 10, Tag(2, "工站温度", 79, false));
        second.TagValues.Single().IsWarning = true;
        store.Records.Add(("202609", first));
        store.Records.Add(("202609", second));
        var service = new ReportService(store);

        Assert.Equal(2, (await service.GetWarningTopAsync(Day1, Day2)).Count);
        Assert.Single(await service.GetWarningTopAsync(Day1, Day2, take: 1));
        // 第二条在 Day1+1h，收窄到 30 分钟就只剩第一条。
        Assert.Equal("压力", Assert.Single(await service.GetWarningTopAsync(Day1, Day1.AddMinutes(30))).Name);
        Assert.Empty(await service.GetWarningTopAsync(Day1.AddHours(5), Day2));
    }

    [Fact]
    public async Task Warning_top_returns_empty_when_nothing_entered_the_band()
    {
        var service = new ReportService(BuildStore());

        // BuildStore 的数据都是超规格或正常值，没有人落在黄区。
        Assert.Empty(await service.GetWarningTopAsync(Day1, Day2.AddDays(1)));
    }
}
