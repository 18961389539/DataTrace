using DataTrace.Application.Realtime;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public class DashboardFilterTests : WebTestBase
{
    private IRenderedComponent<Dashboard> Render(bool shopFloor = false)
    {
        var hub = new Mock<IRuntimeStatusHub>();
        hub.SetupGet(status => status.Stations).Returns(
        [
            Station(1, StationRuntimeState.Fault),
            Station(2, StationRuntimeState.Busy, Judgement.Ng),
            Station(3, StationRuntimeState.Idle, Judgement.Ng),
            Station(4, StationRuntimeState.Disabled, Judgement.Ng)
        ]);
        hub.SetupGet(status => status.Plcs).Returns([]);
        hub.SetupGet(status => status.Recent).Returns([]);
        hub.SetupGet(status => status.OpenSessions).Returns([]);
        hub.SetupGet(status => status.InProcessRecipes).Returns([]);
        hub.SetupGet(status => status.LastCollectorAt).Returns(DateTime.Now);

        var reports = new Mock<IReportService>();
        reports.Setup(service => service.GetPieceYieldAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PieceYieldReport());
        reports.Setup(service => service.GetShiftStopsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShiftStopReport());
        var spool = new Mock<ISpoolStore>();
        spool.Setup(store => store.DescribeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SpoolBacklog(0, null));

        Context.Services.AddSingleton(hub.Object);
        Context.Services.AddSingleton(reports.Object);
        Context.Services.AddSingleton(spool.Object);
        RenderPopoverHost();
        return Context.Render<Dashboard>(parameters => parameters.AddCascadingValue("ShopFloor", shopFloor));
    }

    private static StationRuntimeStatus Station(int id, StationRuntimeState state, Judgement judgement = Judgement.None) => new()
    {
        StationId = id,
        StationCode = $"ST{id:000}",
        StationName = $"工站 {id}",
        Sequence = id,
        State = state,
        LastJudgement = judgement
    };

    [Fact]
    public void FiltersStationsByCurrentPriorityAndCanResetAnEmptyResult()
    {
        var cut = Render();
        Assert.Equal(4, cut.FindAll(".dash-station").Count);

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("需关注")).Click();
        Assert.Equal(2, cut.FindAll(".dash-station").Count);
        Assert.Contains("显示 2 / 4 台", cut.Markup);

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("采集中")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".dash-station"));
            Assert.Contains("ST002", cut.Markup);
        });

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("空闲")).Click();
        Assert.Empty(cut.FindAll(".dash-station"));
        Assert.Contains("当前没有空闲的工站", cut.Markup);
        cut.FindAll("button").Single(button => button.TextContent.Contains("清除搜索与筛选")).Click();
        Assert.Equal(4, cut.FindAll(".dash-station").Count);
    }

    [Fact]
    public void SearchesStationsByCodeOrNameAndCombinesWithStateFilter()
    {
        var cut = Render();

        cut.Find(".dt-station-search input").Input("工站 3");
        Assert.Single(cut.FindAll(".dash-station"));
        Assert.Contains("ST003", cut.Markup);

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("采集中")).Click();
        Assert.Empty(cut.FindAll(".dash-station"));
        Assert.Contains("没有找到“工站 3”对应的工站（当前筛选：采集中）", cut.Markup);

        cut.Find(".dt-station-search input").Input("ST002");
        Assert.Single(cut.FindAll(".dash-station"));
        Assert.Contains("ST002", cut.Markup);
    }

    [Fact]
    public void ShopFloorShowsEveryStationWithoutFilterControls()
    {
        var cut = Render(shopFloor: true);

        Assert.Equal(4, cut.FindAll(".dash-station").Count);
        Assert.Empty(cut.FindAll(".dt-station-filter"));
    }
}