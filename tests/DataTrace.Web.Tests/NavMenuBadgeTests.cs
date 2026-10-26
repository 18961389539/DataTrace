using Bunit;
using DataTrace.Application.Alarms;
using DataTrace.Application.Quality;
using DataTrace.Domain.Constants;
using DataTrace.Web.Components.Layout;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public sealed class NavMenuBadgeTests : WebTestBase
{
    private readonly Mock<IQualityIncidentService> _incidents = new();
    private readonly Mock<IQualityIncidentActorProvider> _actors = new();

    public NavMenuBadgeTests()
    {
        _actors.Setup(provider => provider.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityIncidentActor("admin", [AppRoles.Administrator]));
        _incidents.Setup(service => service.ListAsync(It.IsAny<QualityIncidentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityIncidentListResult { Items = [], Total = 3 });
    }

    [Fact]
    public void Shows_counts_for_alarms_and_my_open_quality_incidents()
    {
        Context.Services.AddSingleton<IQualityIncidentService>(_incidents.Object);
        Context.Services.AddSingleton<IQualityIncidentActorProvider>(_actors.Object);
        var board = new LineAlarmBoard();
        board.Replace(new LineAlarmSnapshot(
        [
            new LineAlarm("a", LineAlarmKind.HeartbeatStale, "采集心跳已停止"),
            new LineAlarm("b", LineAlarmKind.ConsecutiveNg, "连续不合格")
        ]));
        Context.Services.AddSingleton<ILineAlarmBoard>(board);

        var cut = Context.Render<NavMenu>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".dt-nav-badge").Count));
        Assert.Equal("2", cut.FindAll(".dt-nav-badge")[0].TextContent.Trim());
        // 两个状态（Open + InProgress）各 3 张，角标合并为 6。
        Assert.Equal("6", cut.FindAll(".dt-nav-badge")[1].TextContent.Trim());
    }

    [Fact]
    public void Viewer_sees_no_quality_badge()
    {
        Context.Services.AddSingleton<IQualityIncidentService>(_incidents.Object);
        Context.Services.AddSingleton<IQualityIncidentActorProvider>(_actors.Object);
        _actors.Setup(provider => provider.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityIncidentActor("viewer", [AppRoles.Viewer]));
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = Context.Render<NavMenu>();

        Assert.Empty(cut.FindAll(".dt-nav-badge"));
    }
}
