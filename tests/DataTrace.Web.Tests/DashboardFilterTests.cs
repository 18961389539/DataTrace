using DataTrace.Application.Realtime;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
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
        DateTime? collectorAt = null,
        string activeRecipeCode = "")
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
        hub.SetupGet(status => status.ActiveRecipeCode).Returns(activeRecipeCode);

        var reports = new Mock<IReportService>();
        reports.Setup(service => service.GetPieceYieldAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PieceYieldReport());
        reports.Setup(service => service.GetShiftStopsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShiftStopReport());
        reports.Setup(service => service.GetStationJudgementsAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StationJudgementCount>());
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
    public void PinAbnormalStationsAboveSequenceWhenEnabled()
    {
        var stations = new[]
        {
            Station(1, StationRuntimeState.Idle, Judgement.Ok),
            Station(2, StationRuntimeState.Idle, Judgement.Ok),
            Station(3, StationRuntimeState.Fault),
            Station(4, StationRuntimeState.Idle, Judgement.Ng)
        };
        var cut = Render(stations: stations);
        string[] Titles() => cut.FindAll(".dt-station-title").Select(title => title.TextContent.Trim()).ToArray();

        Assert.Equal(["ST001 工站 1", "ST002 工站 2", "ST003 工站 3", "ST004 工站 4"], Titles());

        cut.FindAll(".dt-station-filter").Single(button => button.TextContent.Contains("异常置顶")).Click();
        Assert.Equal(["ST003 工站 3", "ST004 工站 4", "ST001 工站 1", "ST002 工站 2"], Titles());

        cut.FindAll(".dt-station-filters .dt-station-filter").Single(button => button.TextContent.Contains("需关注")).Click();
        Assert.Equal(["ST003 工站 3", "ST004 工站 4"], Titles());
    }

    [Fact]
    public void First_kpi_card_is_labeled_shift_ok()
    {
        var cut = Render();
        Assert.StartsWith("本班OK", cut.Find(".dt-kpi-card .dt-kpi-label").TextContent.Trim());
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
        Assert.All(cut.FindAll(".dt-kpi-card").Take(2),
            card => Assert.Equal("不可用", card.QuerySelector(".dt-kpi-value")?.TextContent.Trim()));
        // 工单卡不依赖班次统计，报表失败时它仍显示档案状态。
        Assert.Equal("无", cut.FindAll(".dt-kpi-card")[2].QuerySelector(".dt-kpi-value")?.TextContent.Trim());
        Assert.All(cut.FindAll(".dt-kpi-updated").Take(2),
            updated => Assert.Contains("暂无成功记录", updated.TextContent));
        Assert.Contains("工单更新于", cut.FindAll(".dt-kpi-updated")[2].TextContent);
    }

    [Fact]
    public void Work_order_card_shows_the_current_order()
    {
        Config.WorkOrders.AddRange(
        [
            new WorkOrder
            {
                Id = 1, Code = "WO-OLD", Status = WorkOrderStatus.InProgress,
                RecipeId = 7, Recipe = new Recipe { Id = 7, Code = "A100", Name = "型号A" },
                PlannedQuantity = 500, StartDate = new DateTime(2026, 10, 20), CreatedAt = new DateTime(2026, 10, 20)
            },
            new WorkOrder
            {
                Id = 2, Code = "WO-NEW", Status = WorkOrderStatus.InProgress, IsCurrent = true,
                RecipeId = 8, Recipe = new Recipe { Id = 8, Code = "B200", Name = "型号B", Model = "MB" },
                PlannedQuantity = 800, StartDate = new DateTime(2026, 10, 28), CreatedAt = new DateTime(2026, 10, 28)
            },
            new WorkOrder
            {
                Id = 3, Code = "WO-WAIT", Status = WorkOrderStatus.Pending,
                CreatedAt = new DateTime(2026, 10, 27)
            }
        ]);
        var cut = Render(activeRecipeCode: "A100");

        // 直通率卡仍带 NG 来源说明。
        Assert.Contains("本班没有不合格件", cut.FindAll(".dt-kpi-card")[1].TextContent);

        var card = cut.FindAll(".dt-kpi-card")[2];
        // 大字就是当前工单号，小字给型号/名称/计划与生产中的张数。
        Assert.Equal("WO-NEW", card.QuerySelector(".dt-kpi-value")?.TextContent.Trim());
        Assert.Contains("MB", card.TextContent);
        Assert.Contains("型号B", card.TextContent);
        Assert.Contains("计划 800 件", card.TextContent);
        // 卡片只讲当前工单，不再汇总生产中的张数。
        Assert.DoesNotContain("生产中 2 张", card.TextContent);
        // 当前工单型号 B200 与当前型号 A100 不一致时会明确提示。
        Assert.Contains("当前型号不一致（当前 A100）", card.TextContent);
        // 没有起点（老数据或未重新设为当前）时不臆造完成数。
        Assert.Contains("完成数从下次设为当前起统计", card.TextContent);
    }

    [Fact]
    public void Work_order_card_estimates_completed_and_remaining_from_current_since()
    {
        var since = DateTime.Now.AddHours(-2);
        Config.WorkOrders.Add(new WorkOrder
        {
            Id = 2, Code = "WO-CUR", Status = WorkOrderStatus.InProgress, IsCurrent = true,
            CurrentSince = since,
            RecipeId = 8, Recipe = new Recipe { Id = 8, Code = "B200", Name = "型号B", Model = "MB" },
            PlannedQuantity = 800
        });
        var cut = Render(activeRecipeCode: "B200", configureReports: reports =>
            reports.Setup(service => service.GetPieceYieldAsync(
                    since, It.IsAny<DateTime>(), null, "B200", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PieceYieldReport
                {
                    ByShift = [new DailyThroughput { Total = 300, Ok = 290, Ng = 10 }]
                }));

        var card = cut.FindAll(".dt-kpi-card")[2];
        Assert.Contains("已完成 300 件（OK 290 · NG 10） · 剩余 500 件", card.TextContent);
    }

    [Fact]
    public void Shift_ng_card_lists_per_station_counts()
    {
        var cut = Render(configureReports: reports =>
            reports.Setup(service => service.GetStationJudgementsAsync(
                    It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<StationJudgementCount>
                {
                    new() { StationId = 2, StationCode = "OP140", Ok = 40, Ng = 7 },
                    new() { StationId = 1, StationCode = "OP130", Ok = 50, Ng = 3 }
                }));

        // 当前间隔卡已被本班 NG 卡替换。
        Assert.DoesNotContain("当前间隔", cut.Markup);
        var card = cut.FindAll(".dt-kpi-card")[3];
        Assert.Contains("本班 NG", card.TextContent);
        Assert.Equal("10", card.QuerySelector(".dt-kpi-value")?.TextContent.Trim());
        Assert.Contains("按工站 NG 数量：OP130 3 件 · OP140 7 件", cut.Markup);

        // 工站卡片也带上本班 OK/NG（默认工站 id=1 为 ST001）。
        Assert.Contains("OK 50", cut.Markup);
        Assert.Contains("NG 3", cut.Markup);
    }

    [Fact]
    public void Work_order_card_has_no_mismatch_note_when_models_match()
    {
        Config.WorkOrders.Add(new WorkOrder
        {
            Id = 2, Code = "WO-NEW", Status = WorkOrderStatus.InProgress, IsCurrent = true,
            RecipeId = 8, Recipe = new Recipe { Id = 8, Code = "B200", Name = "型号B", Model = "MB" },
            PlannedQuantity = 800, CreatedAt = new DateTime(2026, 10, 28)
        });
        var cut = Render(activeRecipeCode: "B200");

        var card = cut.FindAll(".dt-kpi-card")[2];
        Assert.DoesNotContain("当前型号不一致", card.TextContent);
        Assert.DoesNotContain("未设置当前型号", card.TextContent);
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

        Assert.Equal("10 件", cut.Find(".dt-kpi-card .dt-kpi-value").TextContent.Trim());
        Assert.Contains("总数 12 件", cut.Find(".dt-kpi-card .dt-kpi-hint").TextContent);
        var updatedAt = cut.Find(".dt-kpi-updated").TextContent;

        cut.Find(".dt-kpi-refresh button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("统计更新失败，以下为", cut.Markup);
            Assert.Contains("直通率", cut.Markup);
            Assert.Contains("83.3%", cut.Markup);
            Assert.Equal("10 件", cut.Find(".dt-kpi-card .dt-kpi-value").TextContent.Trim());
            Assert.Contains("总数 12 件", cut.Find(".dt-kpi-card .dt-kpi-hint").TextContent);
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
