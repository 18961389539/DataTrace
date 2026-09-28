using DataTrace.Application.Alarms;
using DataTrace.Web.Components.Shared;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

public class LineAlarmBarTests : WebTestBase
{
    [Fact]
    public void Active_alarm_is_shown_and_mute_keeps_the_banner()
    {
        var board = new LineAlarmBoard();
        board.Replace(new LineAlarmSnapshot(
        [
            new LineAlarm("heartbeat", LineAlarmKind.HeartbeatStale, "采集心跳已停止，超过 30 秒没有刷新。")
        ]));
        Context.Services.AddSingleton<ILineAlarmBoard>(board);

        var cut = Context.RenderComponent<LineAlarmBar>();

        Assert.Contains("采集心跳已停止", cut.Markup);
        Assert.Contains("去接手", cut.Markup);
        ClickButton(cut, "静音");

        Assert.Contains("声音已静音，异常仍在。", cut.Markup);
        Assert.Contains("重新响铃", cut.Markup);
    }

    [Fact]
    public void Quiet_board_renders_nothing()
    {
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = Context.RenderComponent<LineAlarmBar>();

        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain("静音", cut.Markup);
    }
}
