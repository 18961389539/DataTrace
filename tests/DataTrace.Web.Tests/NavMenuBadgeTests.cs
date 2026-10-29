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
        var alarmBadge = cut.Find(".dt-nav-badge--alarm");
        Assert.Contains("待接手", alarmBadge.TextContent);
        Assert.Contains("2", alarmBadge.TextContent);
        Assert.Contains("dt-nav-badge--urgent", alarmBadge.ClassName);
        Assert.Equal("待接手 2", alarmBadge.GetAttribute("aria-label"));

        var qualityBadge = cut.Find(".dt-nav-badge--quality");
        // 两个状态（Open + InProgress）各 3 张，角标合并为 6。
        Assert.Equal("6", qualityBadge.TextContent.Trim());
    }

    [Fact]
    public void Viewer_sees_no_quality_badge()
    {
        // AuthorizeView 认的是 bUnit 级联身份；UseRole 只换 AuthenticationStateProvider，两者都要切到访客。
        Context.AddAuthorization().SetAuthorized("viewer").SetRoles(AppRoles.Viewer);
        UseRole(AppRoles.Viewer);
        Context.Services.AddSingleton<IQualityIncidentService>(_incidents.Object);
        Context.Services.AddSingleton<IQualityIncidentActorProvider>(_actors.Object);
        _actors.Setup(provider => provider.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityIncidentActor("viewer", [AppRoles.Viewer]));
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = Context.Render<NavMenu>();

        Assert.Empty(cut.FindAll(".dt-nav-badge"));
        Assert.Contains("访客可查看看板与查询", cut.Markup);
        Assert.Contains("当前为访客视图", cut.Markup);
        Assert.DoesNotContain("现场配置", cut.Markup);
        Assert.DoesNotContain("波形基线", cut.Markup);
        Assert.DoesNotContain("运维工具", cut.Markup);
    }

    [Fact]
    public void Groups_split_field_config_and_ops_tools()
    {
        Context.Services.AddSingleton<IQualityIncidentService>(_incidents.Object);
        Context.Services.AddSingleton<IQualityIncidentActorProvider>(_actors.Object);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = Context.Render<NavMenu>();

        Assert.Contains("现场配置", cut.Markup);
        Assert.Contains("运维工具", cut.Markup);
        Assert.DoesNotContain("系统配置", cut.Markup);
        Assert.DoesNotContain(">账户<", cut.Markup);

        // MudNavLink 在 bUnit 里不一定写出 href，按可见文案核对分组顺序。
        var markup = cut.Markup;
        var production = markup.IndexOf("生产数据", StringComparison.Ordinal);
        var workorders = markup.IndexOf("工单管理", StringComparison.Ordinal);
        var field = markup.IndexOf("现场配置", StringComparison.Ordinal);
        var ops = markup.IndexOf("运维工具", StringComparison.Ordinal);
        var logs = markup.IndexOf("审计日志", StringComparison.Ordinal);
        Assert.True(production >= 0 && workorders > production && workorders < field);
        Assert.True(ops >= 0 && logs > ops);
        Assert.Contains("配置改动即时下发给采集器", cut.Markup);
    }
}