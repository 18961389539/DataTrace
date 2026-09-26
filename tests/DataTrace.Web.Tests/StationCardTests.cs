using DataTrace.Application.Realtime;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Shared;
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

    /// <summary>模拟器跑出来的单件耗时只有几十毫秒，按秒格式化会塌成「0.0 s」。</summary>
    [Fact]
    public void Keeps_millisecond_cadence_readable()
    {
        using var ctx = NewContext();
        var cut = ctx.RenderComponent<StationCard>(p => p.Add(x => x.Station, Station(34)));

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
