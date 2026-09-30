using DataTrace.Application.Realtime;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Shared;
using DataTrace.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 工站卡片的重绘隔离。看板每 5 秒有一次定时重画（用来推进相对时间），
/// 卡片如果跟着每次都重画，点位、tooltip、迷你曲线会被全量 diff ——
/// 大屏常亮在工控机上时这是实打实的开销，所以这里把「数据变了才画」钉住。
/// </summary>
public class StationCardTests
{
    private static TestContext NewContext()
    {
        var ctx = new TestContext();
        // MudChip 渲染后要向 JS 注册按键拦截器，strict 模式下会因为没有计划的调用而抛。
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.RenderComponent<MudPopoverProvider>();
        return ctx;
    }

    private static StationRuntimeStatus Station(int? durationMs = 3200) => new()
    {
        StationId = 1,
        StationCode = "ST010",
        StationName = "上料工站",
        Sequence = 1,
        State = StationRuntimeState.Idle,
        LastJudgement = Judgement.Ok,
        LastPalletCode = "PAL-0001",
        LastSerialNo = "0007",
        LastDurationMs = durationMs,
        LastTags = [new StationLiveTag { Name = "压力", Display = "12.40", Unit = "kN" }]
    };

    private static StationLiveTag Point(string name, bool outOfLimit = false, bool warning = false)
    {
        var value = outOfLimit ? 13.2 : warning ? 11.8 : 11.0;
        return new StationLiveTag
        {
            Name = name,
            Display = value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            Unit = "kN",
            NumericValue = value,
            LowerLimit = 10,
            UpperLimit = 12,
            WarningLowerLimit = 10.5,
            WarningUpperLimit = 11.5,
            OutOfLimit = outOfLimit,
            Warning = warning
        };
    }

    [Fact]
    public void Does_not_repaint_when_the_snapshot_is_unchanged()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station()));
        var before = cut.RenderCount;

        cut.SetParametersAndRender(p => p.Add(x => x.Station, Station()));

        Assert.Equal(before, cut.RenderCount);
    }

    [Fact]
    public void Repaints_when_the_judgement_changes()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station()));
        var before = cut.RenderCount;

        var next = Station();
        next.LastJudgement = Judgement.Ng;
        cut.SetParametersAndRender(p => p.Add(x => x.Station, next));

        Assert.True(cut.RenderCount > before);
    }

    [Fact]
    public void Repaints_when_a_tag_value_changes()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station()));
        var before = cut.RenderCount;

        var next = Station();
        next.LastTags = [new StationLiveTag { Name = "压力", Display = "13.10", Unit = "kN" }];
        cut.SetParametersAndRender(p => p.Add(x => x.Station, next));

        Assert.True(cut.RenderCount > before);
    }

    /// <summary>卡片默认只画一条代表曲线，其他曲线转为提示避免拉高同行卡片。</summary>
    [Fact]
    public void Shows_one_curve_and_hints_at_the_rest()
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastCurves =
        [
            new StationLiveCurve { Name = "压力曲线", Values = [1f, 2f, 3f] },
            new StationLiveCurve { Name = "位移曲线", Values = [1f, 2f, 3f] },
            new StationLiveCurve { Name = "温度曲线", Values = [1f, 2f, 3f] }
        ];

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains("压力曲线", cut.Markup);
        Assert.DoesNotContain("位移曲线", cut.Markup);
        Assert.DoesNotContain("温度曲线", cut.Markup);
        Assert.Contains("还有 2 条曲线", cut.Markup);
    }

    /// <summary>状态不能只靠颜色：色觉障碍用户分不出红绿边条时，图标与文字仍要能分辨。</summary>
    [Theory]
    [InlineData(Judgement.Ok, "OK")]
    [InlineData(Judgement.Ng, "NG")]
    public void Encodes_the_state_with_an_icon_on_top_of_the_colour(Judgement judgement, string label)
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastJudgement = judgement;

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains(label, cut.Markup);
        Assert.Contains("mud-icon-root", cut.Markup);
    }

    [Fact]
    public void Uses_one_fault_state_for_card_and_status_chip()
    {
        using var ctx = NewContext();
        var station = Station();
        station.State = StationRuntimeState.Fault;
        station.LastJudgement = Judgement.Ok;

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains("dash-station fault", cut.Markup);
        Assert.Contains("mud-chip-color-error", cut.Markup);
        Assert.Contains("故障", cut.Markup);
    }

    [Fact]
    public void Shows_the_limit_range_and_numeric_exceedance()
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastTags =
        [
            new StationLiveTag
            {
                Name = "压力",
                Display = "13.2",
                Unit = "kN",
                NumericValue = 13.2,
                LowerLimit = 10,
                UpperLimit = 12,
                WarningLowerLimit = 10.5,
                WarningUpperLimit = 11.5,
                OutOfLimit = true
            }
        ];

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Single(cut.FindAll(".dt-limit-scale"));
        Assert.Contains("高于上限 1.2kN", cut.Find(".dt-tag-exceedance").TextContent);
        Assert.Contains("dt-limit-marker out", cut.Markup);
        Assert.Contains("规格范围 10 至 12", cut.Markup);
        Assert.DoesNotContain("规格范围", cut.Find(".dt-limit-labels").TextContent);
        Assert.Contains("10", cut.Find(".dt-limit-labels").TextContent);
        Assert.Contains("12", cut.Find(".dt-limit-labels").TextContent);
    }

    [Fact]
    public void Keeps_the_full_station_name_available_on_the_compact_title()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station()));

        Assert.Equal("ST010 上料工站", cut.Find(".dt-station-title").TextContent.Trim());
        Assert.Contains("ST010 上料工站", cut.Markup);
    }

    [Fact]
    public void Omits_limit_scale_when_the_record_has_no_numeric_limits()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station()));

        Assert.Empty(cut.FindAll(".dt-limit-scale"));
    }

    [Fact]
    public void Shows_every_point_for_a_station_with_five_points()
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastTags = Enumerable.Range(1, 5).Select(i => Point($"点位{i}")).ToArray();

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Equal(5, cut.FindAll(".dash-tag").Count);
        Assert.Equal(5, cut.FindAll(".dt-limit-scale").Count);
        Assert.Empty(cut.FindAll(".dt-point-expand"));
    }

    [Fact]
    public void Prioritizes_attention_points_and_expands_the_remaining_list()
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastTags =
        [
            Point("正常1"),
            Point("正常2"),
            Point("正常3"),
            Point("正常4"),
            Point("正常5"),
            Point("超限", outOfLimit: true),
            Point("预警", warning: true)
        ];

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        var previewRows = cut.FindAll(".dash-tag");
        Assert.Equal(4, previewRows.Count);
        Assert.Contains("out", previewRows[0].GetAttribute("class"));
        Assert.Contains("warn", previewRows[1].GetAttribute("class"));
        Assert.Equal(2, cut.FindAll(".dt-limit-scale").Count);
        Assert.Contains("查看其余 3 个点位", cut.Markup);

        var expandButton = cut.Find("button.dt-point-expand");
        Assert.Equal("false", expandButton.GetAttribute("aria-expanded"));
        expandButton.Click();

        Assert.Equal(7, cut.FindAll(".dash-tag").Count);
        Assert.Equal(7, cut.FindAll(".dt-limit-scale").Count);
        Assert.Equal("true", cut.Find("button.dt-point-expand").GetAttribute("aria-expanded"));
        Assert.Contains("收起点位", cut.Markup);
    }

    [Fact]
    public void Summarizes_normal_state_once_for_dense_points()
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastTags = Enumerable.Range(1, 7).Select(i => Point($"点位{i}")).ToArray();

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Contains("未标记项均正常", cut.Find(".dt-point-summary-label").TextContent);
        Assert.Empty(cut.FindAll(".dt-point-state.ok"));
        Assert.Equal(4, cut.FindAll(".dash-tag").Count);
    }

    [Fact]
    public void Keeps_all_warning_and_out_of_limit_points_visible()
    {
        using var ctx = NewContext();
        var station = Station();
        station.LastTags =
        [
            Point("超限1", outOfLimit: true),
            Point("超限2", outOfLimit: true),
            Point("预警1", warning: true),
            Point("预警2", warning: true),
            Point("预警3", warning: true),
            Point("正常1"),
            Point("正常2")
        ];

        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, station));

        Assert.Equal(5, cut.FindAll(".dash-tag").Count);
        Assert.Empty(cut.FindAll(".dt-point-state.ok"));
        Assert.Contains("查看其余 2 个点位", cut.Markup);
    }

    /// <summary>模拟器跑出来的单件耗时只有几十毫秒，按秒格式化会塌成「0.0 s」。</summary>
    [Fact]
    public void Keeps_millisecond_cadence_readable()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station(34)));

        Assert.Single(cut.FindAll(".dt-station-subline .dt-station-meta"));
        Assert.Contains("PAL-0001", cut.Find(".dt-station-subline").TextContent);
        Assert.Equal("34 ms", cut.Find(".dt-station-meta-value").TextContent.Trim());
    }

    [Fact]
    public void Switches_to_seconds_for_slower_stations()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station(3450)));

        Assert.Equal("3.5 s", cut.Find(".dt-station-meta-value").TextContent.Trim());
    }
}
