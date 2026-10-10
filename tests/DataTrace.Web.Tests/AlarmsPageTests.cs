using DataTrace.Application.Alarms;
using DataTrace.Domain.Constants;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Services;
using DataTrace.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

public class AlarmsPageTests : WebTestBase
{
    private IRenderedComponent<Alarms> RenderAlarms()
    {
        RenderPopoverHost();
        return Context.Render<Alarms>();
    }

    [Fact]
    public void Taking_an_alarm_records_the_current_user_and_stops_asking()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention =
            [
                new AlarmIncidentDraft
                {
                    Id = 7,
                    Key = "heartbeat",
                    Kind = LineAlarmKind.HeartbeatStale,
                    Message = "采集心跳已停止",
                    RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
                }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();
        cut.WaitForAssertion(() => Assert.Contains("待接手", cut.Markup));
        ClickButton(cut, "接手");

        cut.WaitForAssertion(() => Assert.Contains("已接手，尚未解除", cut.Markup));
        Assert.Equal(7, Assert.Single(incidents.Acknowledged));
        Assert.Equal("admin", incidents.AcknowledgedBy);
        Assert.Contains(Toast.Messages, message => message.Contains("已接手"));
    }

    [Fact]
    public void Ng_alarm_links_to_its_station_and_occurrence_range()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention = [new AlarmIncidentDraft
            {
                Id = 4, Key = "ng:23", Kind = LineAlarmKind.ConsecutiveNg,
                Message = "连续不合格", RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
            }]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();
        var href = cut.FindAll("a").Single(link => link.TextContent.Contains("查看 NG 记录")).GetAttribute("href");

        Assert.Contains("judge=Ng", href);
        Assert.Contains("station=23", href);
        Assert.Contains("from=2026-09-27", href);
        Assert.DoesNotContain("days=30", href);
    }

    [Theory]
    [InlineData(LineAlarmKind.ConsecutiveWarning, "warn:23:45")]
    [InlineData(LineAlarmKind.ProcessDrift, "drift:23:45")]
    public void Process_alarm_links_to_its_station_tag_and_range(LineAlarmKind kind, string key)
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention = [new AlarmIncidentDraft
            {
                Id = 5, Key = key, Kind = kind, Message = "过程异常",
                RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
            }]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();
        var href = cut.FindAll("a").Single(link => link.TextContent.Contains("查看过程报表")).GetAttribute("href");

        Assert.Contains("station=23", href);
        Assert.Contains("tag=45", href);
        Assert.Contains("from=2026-09-27", href);
    }

    [Fact]
    public void Session_alarm_links_to_the_original_session_id()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention = [new AlarmIncidentDraft
            {
                Id = 6, Key = "open:77", Kind = LineAlarmKind.OpenSession,
                Message = "在制超时", RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
            }]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();
        var href = cut.FindAll("a").Single(link => link.TextContent.Contains("打开产品追溯")).GetAttribute("href");

        Assert.Equal("/query?view=trace&sessionId=77", href);
    }

    [Fact]
    public void Viewer_can_see_an_alarm_but_cannot_take_it()
    {
        UseRole(AppRoles.Viewer);
        var incidents = new FakeAlarmIncidents
        {
            Attention =
            [
                new AlarmIncidentDraft
                {
                    Id = 3,
                    Key = "mes",
                    Kind = LineAlarmKind.MesBacklog,
                    Message = "MES 有 4 条记录排队",
                    RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
                }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();

        cut.WaitForAssertion(() => Assert.Contains("只能查看，不能接手", cut.Markup));
        Assert.DoesNotContain("接手</button>", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(incidents.Acknowledged);
    }

    [Fact]
    public void Unacknowledged_and_acknowledged_open_alarms_are_separated()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention =
            [
                new AlarmIncidentDraft
                {
                    Id = 10,
                    Key = "pending",
                    Kind = LineAlarmKind.StationFault,
                    Message = "待接手故障",
                    RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
                },
                new AlarmIncidentDraft
                {
                    Id = 11,
                    Key = "owned",
                    Kind = LineAlarmKind.MesBacklog,
                    Message = "已接手积压",
                    RaisedAt = new DateTime(2026, 9, 28, 16, 1, 0),
                    AcknowledgedAt = new DateTime(2026, 9, 28, 16, 2, 0),
                    AcknowledgedBy = "engineer"
                }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();

        Assert.Contains("待接手 1", cut.Markup);
        Assert.Contains("已接手，尚未解除 1", cut.Markup);
        var tables = cut.FindAll("table");
        Assert.Equal(2, tables.Count);
        Assert.Single(tables[0].QuerySelectorAll("button"));
        Assert.Empty(tables[1].QuerySelectorAll("button"));
    }

    [Fact]
    public async Task Alarm_filters_combine_station_kind_and_current_owner_without_mutating_the_board()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention =
            [
                new AlarmIncidentDraft { Id = 1, Key = "ng:23", Kind = LineAlarmKind.ConsecutiveNg, Message = "本站未接手", RaisedAt = DateTime.Now },
                new AlarmIncidentDraft { Id = 2, Key = "fault:23", Kind = LineAlarmKind.StationFault, Message = "本站我的故障", RaisedAt = DateTime.Now, AcknowledgedAt = DateTime.Now, AcknowledgedBy = "admin" },
                new AlarmIncidentDraft { Id = 3, Key = "fault:24", Kind = LineAlarmKind.StationFault, Message = "其他工站故障", RaisedAt = DateTime.Now },
                new AlarmIncidentDraft { Id = 4, Key = "fault:23", Kind = LineAlarmKind.StationFault, Message = "同站他人故障", RaisedAt = DateTime.Now, AcknowledgedAt = DateTime.Now, AcknowledgedBy = "engineer" }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());
        var cut = RenderAlarms();
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int?>>().Instance.ValueChanged.InvokeAsync(23));
        Assert.DoesNotContain("其他工站故障", cut.Markup);
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<LineAlarmKind?>>().Instance.ValueChanged.InvokeAsync(LineAlarmKind.StationFault));
        Assert.DoesNotContain("本站未接手", cut.Markup);
        await cut.InvokeAsync(() => cut.FindComponent<MudCheckBox<bool>>().Instance.ValueChanged.InvokeAsync(true));
        Assert.Contains("本站我的故障", cut.Markup);
        Assert.DoesNotContain("同站他人故障", cut.Markup);
        Assert.Equal(4, incidents.Attention.Count);
        ClickButton(cut, "清空筛选");
        Assert.Contains("其他工站故障", cut.Markup);
    }

    [Fact]
    public void Restored_alarm_duration_stops_at_clear_time_not_acknowledgement()
    {
        var incidents = new FakeAlarmIncidents { Attention = [new AlarmIncidentDraft
        {
            Id = 1, Key = "ng:23", Kind = LineAlarmKind.ConsecutiveNg, Message = "恢复等待接手",
            RaisedAt = new DateTime(2026, 9, 28, 20, 0, 0), ClearedAt = new DateTime(2026, 9, 28, 22, 35, 0)
        }] };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());
        var cut = RenderAlarms();
        Assert.Contains("2 小时 35 分", cut.Markup);
        Assert.Contains("已恢复，待接手", cut.Markup);
    }

    private sealed class FakeAlarmIncidents : IAlarmIncidents
    {
        public List<AlarmIncidentDraft> Attention { get; set; } = [];

        public List<long> Acknowledged { get; } = [];

        public string? AcknowledgedBy { get; private set; }

        public Task<IReadOnlyList<AlarmIncidentDraft>> ListAttentionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlarmIncidentDraft>>(Attention);

        public Task<IReadOnlyList<AlarmIncidentDraft>> ListRecentClosedAsync(int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlarmIncidentDraft>>([]);

        public Task RaiseAsync(LineAlarm alarm, DateTime now, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AlarmIncidentDraft>> ApplyAsync(
            IReadOnlyList<LineAlarm> active,
            DateTime now,
            CancellationToken cancellationToken = default)
            => ListAttentionAsync(cancellationToken);

        public Task<AlarmAckResult> AcknowledgeAsync(long id, string userName, CancellationToken cancellationToken = default)
        {
            Acknowledged.Add(id);
            AcknowledgedBy = userName;
            Attention = Attention
                .Select(item => item.Id == id
                    ? item with { AcknowledgedAt = new DateTime(2026, 9, 28, 16, 5, 0), AcknowledgedBy = userName }
                    : item)
                .ToList();
            return Task.FromResult(new AlarmAckResult { Found = true });
        }
    }
}
