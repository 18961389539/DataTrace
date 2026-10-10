using DataTrace.Application.Realtime;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public class DashboardSearchTests : WebTestBase
{
    private IRenderedComponent<Dashboard> Render()
    {
        var hub = new Mock<IRuntimeStatusHub>();
        hub.SetupGet(status => status.Stations).Returns(
        [
            Station(1, "ST010", "上料工站"),
            Station(2, "ST020", "压装工站")
        ]);
        hub.SetupGet(status => status.Plcs).Returns([]);
        hub.SetupGet(status => status.Recent).Returns([]);
        hub.SetupGet(status => status.OpenSessions).Returns([]);
        hub.SetupGet(status => status.InProcessRecipes).Returns([]);
        hub.SetupGet(status => status.LastCollectorAt).Returns(DateTime.Now);

        var reports = new Mock<IReportService>();
        reports.Setup(service => service.GetPieceYieldAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PieceYieldReport());
        reports.Setup(service => service.GetShiftStopsAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShiftStopReport());

        var spool = new Mock<ISpoolStore>();
        spool.Setup(store => store.DescribeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolBacklog(0, null));

        Context.Services.AddSingleton(hub.Object);
        Context.Services.AddSingleton(reports.Object);
        Context.Services.AddSingleton(spool.Object);
        RenderPopoverHost();
        return Context.Render<Dashboard>();
    }

    [Fact]
    public void SearchesByStationCodeOrNameAndRecoversFromNoResults()
    {
        var cut = Render();
        Assert.Equal(2, cut.FindAll(".dash-station").Count);

        TypeInto(cut, "查找工站", "st010");
        Assert.Single(cut.FindAll(".dash-station"));
        Assert.Contains("ST010", cut.Markup);
        Assert.DoesNotContain("ST020", cut.Markup);

        TypeInto(cut, "查找工站", "压装");
        Assert.Single(cut.FindAll(".dash-station"));
        Assert.Contains("ST020", cut.Markup);

        TypeInto(cut, "查找工站", "不存在");
        Assert.Empty(cut.FindAll(".dash-station"));
        Assert.Contains("没有找到“不存在”对应的工站", cut.Markup);

        TypeInto(cut, "查找工站", "");
        Assert.Equal(2, cut.FindAll(".dash-station").Count);
    }

    private static StationRuntimeStatus Station(int id, string code, string name) => new()
    {
        StationId = id,
        StationCode = code,
        StationName = name,
        Sequence = id,
        State = StationRuntimeState.Idle,
        LastJudgement = Judgement.Ok
    };

}