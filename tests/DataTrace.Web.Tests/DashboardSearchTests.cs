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
    public void Uses_full_width_cards_and_only_enabled_curve_configuration()
    {
        Config.Snapshot = new DataTrace.Application.Configuration.AppConfigurationSnapshot
        {
            Stations =
            [
                new DataTrace.Domain.Entities.Station
                {
                    Id = 1, Code = "ST010", Name = "上料工站",
                    Curves =
                    [
                        new DataTrace.Domain.Entities.CurveDefinition { Id = 10, PositionIndex = 1, Name = "定位曲线", Enabled = true },
                        new DataTrace.Domain.Entities.CurveDefinition { Id = 11, PositionIndex = 1, Name = "停用曲线", Enabled = false }
                    ]
                },
                new DataTrace.Domain.Entities.Station { Id = 2, Code = "ST020", Name = "压装工站" }
            ]
        };
        var cut = Render();
        var items = cut.FindAll(".dt-station-grid > .mud-grid-item");
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Contains("mud-grid-item-xs-12", item.ClassName));
        Assert.Single(cut.FindAll(".dt-station-curves"));
        Assert.Contains("定位曲线", cut.Markup);
        Assert.Contains("本次暂无可显示数据", cut.Markup);
        Assert.DoesNotContain("停用曲线", cut.Markup);
        Assert.DoesNotContain("概览行数", cut.Markup);
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
