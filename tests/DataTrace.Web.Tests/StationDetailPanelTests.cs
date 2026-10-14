using DataTrace.Application.Realtime;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Realtime;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 工站详情弹窗内容：四个页签 + 跟随最新/固定本次。
/// 固定的是「整份采集快照」—— 参数与曲线同进同退，新数据只提示不覆盖。
/// </summary>
public class StationDetailPanelTests
{
    private static TestContext NewContext()
    {
        var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        return ctx;
    }

    private static StationRuntimeStatus Station(string serial, long recordId, bool withOutLimit = true) => new()
    {
        StationId = 1,
        StationCode = "ST03",
        StationName = "压装站",
        Sequence = 3,
        State = StationRuntimeState.Idle,
        LastJudgement = withOutLimit ? Judgement.Ng : Judgement.Ok,
        LastPalletCode = "PAL-9",
        LastSerialNo = serial,
        LastRecordId = recordId,
        LastMonthKey = "202610",
        LastResultCode = 11,
        LastTags = withOutLimit
            ?
            [
                new StationLiveTag
                {
                    Name = "压力峰值",
                    Display = "18.4",
                    Unit = "MPa",
                    NumericValue = 18.4,
                    LowerLimit = 10,
                    UpperLimit = 18,
                    OutOfLimit = true
                },
                new StationLiveTag { Name = "终点位移", Display = "12.6", Unit = "mm", NumericValue = 12.6 }
            ]
            :
            [
                new StationLiveTag { Name = "终点位移", Display = "12.6", Unit = "mm", NumericValue = 12.6 }
            ],
        LastCurves =
        [
            new StationLiveCurve
            {
                Name = "压装曲线",
                Key = "5:1",
                Values = [1f, 2f, 3f],
                Judgement = withOutLimit ? Judgement.Ng : Judgement.Ok,
                JudgementReason = withOutLimit ? "压装曲线波形异常：峰值超基线 3σ" : null
            }
        ]
    };

    [Fact]
    public async Task Renders_four_tabs_and_issue_tab_by_default()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationDetailPanel>(p => p
            .Add(x => x.Station, Station("SN123", 77)));

        var tabs = cut.FindAll(".dt-detail-tab");
        Assert.Equal(4, tabs.Count);
        Assert.Contains("异常（2）", tabs[0].TextContent);
        Assert.Contains("参数（2）", tabs[1].TextContent);
        Assert.Contains("曲线（1）", tabs[2].TextContent);
        Assert.Contains("诊断", tabs[3].TextContent);
        // 异常页签默认激活：超限点位与 NG 曲线原因都直接可见。
        Assert.Contains("压力峰值", cut.Markup);
        Assert.Contains("压装曲线波形异常：峰值超基线 3σ", cut.Markup);
    }

    [Fact]
    public async Task Pin_freezes_the_snapshot_and_hints_at_new_data()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationDetailPanel>(p => p
            .Add(x => x.Station, Station("SN123", 77, withOutLimit: true)));

        // 切到「已固定本次」。
        cut.Find(".dt-detail-pin").Click();

        // 新的一件完成（超限消失）：固定快照不变 —— 异常行还在；只出现新数据提示。
        cut.Render(p => p.Add(x => x.Station, Station("SN999", 88, withOutLimit: false)));

        Assert.Contains("已固定本次", cut.Markup);
        Assert.Contains("压力峰值", cut.Markup);
        Assert.Contains("已有新采集完成（SN999）", cut.Markup);

        // 采回跟随：视图立刻换成新件 —— 上一件的超限行消失。
        cut.Find(".dt-detail-pin").Click();
        Assert.Contains("跟随最新", cut.Markup);
        Assert.DoesNotContain("压力峰值", cut.Markup);
        Assert.DoesNotContain("已有新采集完成", cut.Markup);
    }

    [Fact]
    public async Task Curve_tab_shows_stacked_charts_with_reason()
    {
        await using var ctx = NewContext();
        var station = Station("SN123", 77);
        station.LastCurves =
        [
            .. station.LastCurves,
            new StationLiveCurve { Name = "位移曲线", Key = "6:1", Values = [4f, 5f, 6f], Judgement = Judgement.Ok }
        ];
        var cut = ctx.Render<StationDetailPanel>(p => p
            .Add(x => x.Station, station));

        // 切到曲线页签（自绘页签按钮的第 3 个）。
        cut.FindAll(".dt-detail-tab")[2].Click();

        // 每条曲线一张独立图（不同单位天然分图），NG 曲线图下带原因。
        Assert.Equal(2, cut.FindAll(".dt-curve-block").Count);
        Assert.Contains("曲线 压装曲线", cut.FindAll(".dt-curve-block")[0].GetAttribute("aria-label"));
        Assert.Contains("曲线 位移曲线", cut.FindAll(".dt-curve-block")[1].GetAttribute("aria-label"));
        Assert.Contains("压装曲线波形异常：峰值超基线 3σ", cut.Markup);
        Assert.Contains("打开整份采集记录（历史曲线与原始数据）", cut.Markup);
    }

    [Fact]
    public async Task Parameter_tab_supports_search_and_issue_only()
    {
        await using var ctx = NewContext();
        var cut = ctx.Render<StationDetailPanel>(p => p
            .Add(x => x.Station, Station("SN123", 77)));

        // 切到参数页签。
        cut.FindAll(".dt-detail-tab")[1].Click();
        Assert.Contains("超限（1）", cut.Markup);

        // 搜索「位移」：超限组消失，只剩正常组。
        cut.Find(".dt-detail-search input").Input("位移");
        Assert.DoesNotContain("超限（1）", cut.Markup);
        Assert.Contains("终点位移", cut.Markup);

        // 清空搜索、打开「仅看异常」：正常组消失。
        cut.Find(".dt-detail-search input").Input("");
        cut.Find(".mud-switch input").Change(true);
        Assert.DoesNotContain("正常（1）", cut.Markup);
        Assert.Contains("超限（1）", cut.Markup);
    }

    [Fact]
    public async Task Opens_inside_centered_mud_dialog()
    {
        await using var ctx = NewContext();
        ctx.Services.AddSingleton<IRuntimeStatusHub>(new RuntimeStatusHub());
        ctx.Services.AddSingleton<IDialogService, DialogService>();
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();

        await dialogs.ShowAsync<StationDetailDialog>("ST03 压装站", new DialogParameters
        {
            ["Station"] = Station("SN123", 77)
        }, new DialogOptions
        {
            MaxWidth = MaxWidth.Large,
            FullWidth = true,
            CloseButton = true,
            CloseOnEscapeKey = true
        });
        provider.Render();

        Assert.Contains("dt-station-dialog", provider.Markup);
        Assert.Contains("ST03 压装站", provider.Markup);
        Assert.Contains("dt-detail-tabs", provider.Markup);
    }
}
