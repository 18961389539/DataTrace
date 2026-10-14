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
    private IRenderedComponent<Dashboard> Render(
        bool shopFloor = false,
        IReadOnlyList<StationRuntimeStatus>? stations = null,
        Action<Mock<IReportService>>? configureReports = null,
        DateTime? collectorAt = null)
    {
        var hub = new Mock<IRuntimeStatusHub>();
        hub.SetupGet(status => status.Stations).Returns(stations ??
        [
            Station(1, StationRuntimeState.Fault),
            Station(2, StationRuntimeState.Busy, Judgement.Ng),
            Station(3, StationRuntimeState.Idle, Judgement.Ng),
            Station(4, StationRuntimeState.Disabled)
        ]);
        hub.SetupGet(status => status.Plcs).Returns([]);
        hub.SetupGet(status => status.Recent).Returns([]);
        hub.SetupGet(status => status.OpenSessions).Returns([]);
        hub.SetupGet(status => status.InProcessRecipes).Returns([]);
        hub.SetupGet(status => status.LastCollectorAt).Returns(collectorAt ?? DateTime.Now);

        var reports = new Mock<IReportService>();
        reports.Setup(service => service.GetPieceYieldAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PieceYieldReport());
        reports.Setup(service => service.GetShiftStopsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShiftStopReport());
        configureReports?.Invoke(reports);
        var spool = new Mock<ISpoolStore>();
        spool.Setup(store => store.DescribeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SpoolBacklog(0, null));

        Context.Services.AddSingleton(hub.Object);
        Context.Services.AddSingleton(reports.Object);
        Context.Services.AddSingleton(spool.Object);
        RenderPopoverHost();
        return Context.Render<Dashboard>(parameters => parameters.AddCascadingValue("ShopFloor", shopFloor));
    }

    private static StationRuntimeStatus Station(
        int id,
        StationRuntimeState state,
        Judgement judgement = Judgement.None,
        IReadOnlyList<StationLiveTag>? tags = null) => new()
    {
        StationId = id,
        StationCode = $"ST{id:000}",
        StationName = $"工站 {id}",
        Sequence = id,
        State = state,
        LastJudgement = judgement,
        LastTags = tags ?? []
    };

    [Fact]
    public void FiltersStationsByAttentionAndCurrentRuntimeState()
    {
        var cut = Render();
        Assert.Equal(4, cut.FindAll(".dash-station").Count);

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("需关注")).Click();
        Assert.Equal(3, cut.FindAll(".dash-station").Count);
        Assert.Contains("显示 3 / 4 台", cut.Markup);

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("采集中")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Single(cut.FindAll(".dash-station"));
            Assert.Contains("ST002", cut.Markup);
        });

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("空闲")).Click();
        Assert.Single(cut.FindAll(".dash-station"));
        Assert.Contains("ST003", cut.Markup);
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
        Assert.Empty(cut.FindAll(".dt-station-filters .dt-station-filter"));
    }

    [Fact]
    public void ThirtyStationOverviewKeepsEverySummaryVisibleInStableLineOrder()
    {
        var stations = Enumerable.Range(1, 30)
            .Select(id => Station(
                id,
                id == 30 ? StationRuntimeState.Fault : StationRuntimeState.Idle,
                id == 29 ? Judgement.Ng : Judgement.Ok,
                [Tag($"关键参数{id}", outOfLimit: id == 30)]))
            .ToArray();

        var cut = Render(shopFloor: true, stations: stations);
        var cards = cut.FindAll(".dash-station");
        var titles = cards.Select(card => card.QuerySelector(".dt-station-title")!.TextContent.Trim()).ToArray();

        Assert.Equal(30, cards.Count);
        Assert.Equal(Enumerable.Range(1, 30).Select(id => $"ST{id:000} 工站 {id}"), titles);
        Assert.Equal(30, cut.FindAll(".dt-station-overview").Count);
        Assert.Empty(cut.FindAll(".dt-station-parameters"));
        Assert.Contains("关键参数29", cards[28].TextContent);
        Assert.Contains("最近产品 NG", cards[28].TextContent);
        Assert.Contains("运行故障", cards[29].TextContent);
        Assert.Contains("关键参数30", cards[29].TextContent);
    }

    [Fact]
    public void AttentionIncludesFaultsBusyPreviousNgAndLiveTagIssues()
    {
        var stations = new[]
        {
            Station(1, StationRuntimeState.Fault),
            Station(2, StationRuntimeState.Busy, Judgement.Ng),
            Station(3, StationRuntimeState.Idle, Judgement.Ng),
            Station(4, StationRuntimeState.Disabled, Judgement.Ok),
            Station(5, StationRuntimeState.Busy, tags: [Tag("预警", warning: true)]),
            Station(6, StationRuntimeState.Busy, tags: [Tag("超限", outOfLimit: true)]),
            Station(7, StationRuntimeState.Idle, tags: [Tag("无数据", noData: true)]),
            Station(8, StationRuntimeState.Busy, Judgement.Ok)
        };

        var cut = Render(stations: stations);
        var attentionButton = cut.FindAll(".dt-station-filters .dt-station-filter")
            .Single(button => button.TextContent.Contains("需关注"));
        Assert.Contains("6", attentionButton.TextContent);

        attentionButton.Click();
        Assert.Equal(6, cut.FindAll(".dash-station").Count);
        Assert.Contains("ST002", cut.Markup);
        Assert.Contains("ST005", cut.Markup);
        Assert.Contains("ST006", cut.Markup);
        Assert.Contains("ST007", cut.Markup);
        Assert.DoesNotContain("ST008", cut.Markup);

        var busyButton = cut.FindAll(".dt-station-filters .dt-station-filter")
            .Single(button => button.TextContent.Contains("采集中"));
        Assert.Contains("4", busyButton.TextContent);
    }

    [Fact]
    public void InitialKpiFailureShowsUnavailableIndependentlyOfCollectorHeartbeat()
    {
        var cut = Render(
            collectorAt: DateTime.Now,
            configureReports: reports => reports
                .Setup(service => service.GetPieceYieldAsync(
                    It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null, It.IsAny<CancellationToken>()))
                .Returns(Task.FromException<PieceYieldReport>(new InvalidOperationException("report unavailable"))));

        Assert.Contains("心跳正常", cut.Markup);
        Assert.Contains("统计更新失败，统计暂不可用。", cut.Markup);
        Assert.All(cut.FindAll(".dt-kpi-card").Take(3),
            card => Assert.Equal("不可用", card.QuerySelector(".dt-kpi-value")?.TextContent.Trim()));
        Assert.All(cut.FindAll(".dt-kpi-updated"),
            updated => Assert.Contains("暂无成功记录", updated.TextContent));
    }

    [Fact]
    public void LaterKpiFailureKeepsLastSuccessfulNumbersAndTimestampWithRetry()
    {
        var calls = 0;
        var cut = Render(configureReports: reports => reports
            .Setup(service => service.GetPieceYieldAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                calls++;
                return calls == 1
                    ? Task.FromResult(new PieceYieldReport
                    {
                        ByShift = [new DailyThroughput { Total = 12, Ok = 10, Ng = 2, QualityNg = 2 }]
                    })
                    : Task.FromException<PieceYieldReport>(new InvalidOperationException("report unavailable"));
            }));

        Assert.Equal("12 件", cut.Find(".dt-kpi-card .dt-kpi-value").TextContent.Trim());
        var updatedAt = cut.Find(".dt-kpi-updated").TextContent;

        cut.Find(".dt-kpi-refresh button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("统计更新失败，以下为", cut.Markup);
            Assert.Contains("直通率", cut.Markup);
            Assert.Contains("83.3%", cut.Markup);
            Assert.Equal("12 件", cut.Find(".dt-kpi-card .dt-kpi-value").TextContent.Trim());
            Assert.Equal(updatedAt, cut.Find(".dt-kpi-updated").TextContent);
            Assert.Contains("重试统计", cut.Markup);
            Assert.Contains("心跳正常", cut.Markup);
        });
    }

    private static StationLiveTag Tag(string name, bool warning = false, bool outOfLimit = false, bool noData = false)
        => new()
        {
            Name = name,
            Display = noData ? "-" : "11",
            NumericValue = noData ? null : 11,
            TextValue = null,
            LowerLimit = 10,
            UpperLimit = 12,
            Warning = warning,
            OutOfLimit = outOfLimit
        };
}
