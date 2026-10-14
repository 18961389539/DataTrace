using DataTrace.Application.Alarms;
using DataTrace.Web.Components.Shared;
using DataTrace.Shared;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

public class LineAlarmBarTests : WebTestBase
{
    [Fact]
    public void Summary_is_compact_separates_recovered_alarms_and_keeps_controls()
    {
        var board = new LineAlarmBoard();
        board.Replace(new LineAlarmSnapshot(
        [
            new LineAlarm("ng:1", LineAlarmKind.ConsecutiveNg, "工站 OP20 连续 3 件质量不合格。"),
            new LineAlarm("fault:2", LineAlarmKind.StationFault, "工站 OP30 采集故障：PLC 无响应。（已恢复，仍待接手）")
        ]));
        Context.Services.AddSingleton<ILineAlarmBoard>(board);

        var cut = Context.Render<LineAlarmBar>();

        var summary = cut.Find("[role=status]");
        Assert.Contains("当前异常 1", summary.TextContent);
        Assert.Contains("已恢复待确认 1", summary.TextContent);
        Assert.True(cut.Find("#dt-alarm-details").HasAttribute("hidden"));
        Assert.Empty(cut.FindAll("[role=alert]"));
        Assert.NotNull(cut.Find("a[href='/alarms']"));

        ClickButton(cut, "静音");

        Assert.Contains("声音已静音", cut.Find("[role=status]").TextContent);
        Assert.Contains("重新响铃", cut.Markup);

        ClickButton(cut, "展开详情");
        Assert.False(cut.Find("#dt-alarm-details").HasAttribute("hidden"));
        Assert.Contains("当前仍在发生（1）", cut.Find("#dt-alarm-details").TextContent);
        Assert.Contains("已恢复，待接手（1）", cut.Find("#dt-alarm-details").TextContent);
        Assert.Contains("工站 OP30 采集故障：PLC 无响应。", cut.Find("#dt-alarm-details").TextContent);
        Assert.DoesNotContain("已恢复，仍待接手", cut.Find("#dt-alarm-details").TextContent);
    }

    [Fact]
    public void Only_current_collection_blockers_are_announced_assertively()
    {
        var board = new LineAlarmBoard();
        board.Replace(new LineAlarmSnapshot(
        [
            new LineAlarm("heartbeat", LineAlarmKind.HeartbeatStale, "采集心跳已停止。"),
            new LineAlarm("fault:1", LineAlarmKind.StationFault, "工站 OP20 采集故障：PLC 无响应。"),
            new LineAlarm("ng:2", LineAlarmKind.ConsecutiveNg, "工站 OP30 连续 3 件质量不合格。"),
            new LineAlarm("collection:3", LineAlarmKind.CollectionOverdue, "工站 OP40 采集任务超时。（已恢复，仍待接手）")
        ]));
        Context.Services.AddSingleton<ILineAlarmBoard>(board);

        var cut = Context.Render<LineAlarmBar>();

        var alert = Assert.Single(cut.FindAll("[role=alert]"));
        Assert.Equal("assertive", alert.GetAttribute("aria-live"));
        Assert.Contains("采集心跳已停止", alert.TextContent);
        Assert.Contains("工站 OP20 采集故障", alert.TextContent);
        Assert.DoesNotContain("工站 OP30 连续 3 件质量不合格", alert.TextContent);
        Assert.DoesNotContain("工站 OP40 采集任务超时", alert.TextContent);
        Assert.Contains("采集阻断 2", cut.Find("[role=status]").TextContent);
    }

    [Fact]
    public void Expanded_details_include_all_current_alarms()
    {
        var board = new LineAlarmBoard();
        board.Replace(new LineAlarmSnapshot(
            Enumerable.Range(1, 7)
                .Select(i => new LineAlarm($"ng:{i}", LineAlarmKind.ConsecutiveNg, $"工站 {i} 连续不合格"))
                .ToList()));
        Context.Services.AddSingleton<ILineAlarmBoard>(board);

        var cut = Context.Render<LineAlarmBar>();

        Assert.Contains("当前异常 7", cut.Find("[role=status]").TextContent);
        ClickButton(cut, "展开详情");

        foreach (var alarmNumber in Enumerable.Range(1, 7))
        {
            Assert.Contains($"工站 {alarmNumber} 连续不合格", cut.Find("#dt-alarm-details").TextContent);
        }
    }

    [Fact]
    public void Quiet_board_renders_nothing()
    {
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = Context.Render<LineAlarmBar>();

        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain("静音", cut.Markup);
    }
}
