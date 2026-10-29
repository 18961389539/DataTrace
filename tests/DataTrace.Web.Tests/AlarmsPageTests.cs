using DataTrace.Application.Alarms;
using DataTrace.Domain.Constants;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Services;
using DataTrace.Shared;
using Microsoft.AspNetCore.Components;
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
    public void Status_view_is_written_to_the_url_for_refresh_and_back_navigation()
    {
        Context.Services.AddSingleton<IAlarmIncidents>(new FakeAlarmIncidents());
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("button"),
            button => button.TextContent.Contains("当前／仍在发生")));

        cut.FindAll("button").First(button => button.TextContent.Contains("当前／仍在发生")).Click();

        cut.WaitForAssertion(() => Assert.Contains("view=Current",
            Context.Services.GetRequiredService<NavigationManager>().Uri));
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
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "接手").Click();

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

    [Fact]
    public async Task Alarm_status_views_prioritize_current_alarms_and_page_large_lists()
    {
        var now = DateTime.Now;
        var recovered = Enumerable.Range(0, 100)
            .Select(index => new AlarmIncidentDraft
            {
                Id = index + 1,
                Key = $"warning:{index + 1}",
                Kind = LineAlarmKind.ConsecutiveWarning,
                Message = $"恢复告警 {index:000}",
                RaisedAt = now.AddMinutes(-index - 1),
                ClearedAt = now.AddMinutes(-index)
            })
            .ToList();
        recovered.Add(new AlarmIncidentDraft
        {
            Id = 101,
            Key = "fault:23",
            Kind = LineAlarmKind.StationFault,
            Message = "较早发生的紧急当前故障",
            RaisedAt = now.AddDays(-30)
        });
        recovered.Add(new AlarmIncidentDraft
        {
            Id = 102,
            Key = "heartbeat",
            Kind = LineAlarmKind.HeartbeatStale,
            Message = "已接手的当前心跳故障",
            RaisedAt = now,
            AcknowledgedAt = now,
            AcknowledgedBy = "admin"
        });
        var incidents = new FakeAlarmIncidents
        {
            Attention = recovered,
            Recent =
            [
                new AlarmIncidentDraft
                {
                    Id = 201, Key = "closed:1", Kind = LineAlarmKind.MesBacklog, Message = "已解除 MES 报警",
                    RaisedAt = now.AddHours(-2), AcknowledgedAt = now.AddHours(-1), AcknowledgedBy = "engineer",
                    ClearedAt = now.AddMinutes(-30)
                },
                new AlarmIncidentDraft
                {
                    Id = 202, Key = "closed:2", Kind = LineAlarmKind.ConsecutiveNg, Message = "已解除 NG 报警",
                    RaisedAt = now.AddHours(-4), AcknowledgedAt = now.AddHours(-3), AcknowledgedBy = "admin",
                    ClearedAt = now.AddHours(-2)
                }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();

        Assert.Contains("当前／仍在发生 (2)", cut.Markup);
        Assert.Contains("已恢复待接手 (100)", cut.Markup);
        Assert.Contains("已接手／已解除 (3)", cut.Markup);
        Assert.Contains("当前模式：显示全部待处理记录，以及最近 30 条已解除记录", cut.Markup);

        var attentionRows = cut.FindAll(".dt-needs-owner-alarms--system table").SelectMany(table => table.QuerySelectorAll("tbody tr")).ToList();
        Assert.Equal(25, attentionRows.Count);
        Assert.Contains("恢复告警 000", cut.Markup);
        Assert.Contains("每页 25 条", cut.Markup);
        Assert.Contains("系统规则报警", cut.Markup);
        Assert.Contains("设备报警", cut.Markup);

        await cut.InvokeAsync(() => cut.FindComponents<MudPagination>()[0].Instance.SelectedChanged.InvokeAsync(2));
        attentionRows = cut.FindAll(".dt-needs-owner-alarms--system table").SelectMany(table => table.QuerySelectorAll("tbody tr")).ToList();
        Assert.Equal(25, attentionRows.Count);
        Assert.Contains("恢复告警 024", cut.Markup);

        ClickButton(cut, "已恢复待接手");
        Assert.Contains("已恢复，待接手 100", cut.Markup);
        Assert.DoesNotContain("较早发生的紧急当前故障", cut.Markup);
        Assert.Equal("true", cut.FindAll("button")
            .Single(button => button.TextContent.Contains("已恢复待接手")).GetAttribute("aria-pressed"));

        ClickButton(cut, "已接手／已解除");
        Assert.Contains("已接手，尚未解除 1", cut.Markup);
        Assert.Contains("最近已解除 2", cut.Markup);
        Assert.DoesNotContain("恢复告警 000", cut.Markup);
    }


    [Fact]
    public void Custom_device_alarms_render_in_the_primary_device_region_above_system_rules()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention =
            [
                new AlarmIncidentDraft
                {
                    Id = 1,
                    Key = "custom:23:9",
                    Kind = LineAlarmKind.CustomDevice,
                    Message = "工站 OP10 压力过高：当前 12，限值 10（高于）",
                    RaisedAt = new DateTime(2026, 9, 28, 16, 0, 0)
                },
                new AlarmIncidentDraft
                {
                    Id = 2,
                    Key = "abandon:88",
                    Kind = LineAlarmKind.SessionSuperseded,
                    Message = "会话被抢占",
                    RaisedAt = new DateTime(2026, 9, 28, 16, 1, 0)
                }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());

        var cut = RenderAlarms();

        cut.WaitForAssertion(() => Assert.Contains("工站 OP10 压力过高", cut.Markup));
        Assert.Contains("dt-alarms-region--device", cut.Markup);
        Assert.Contains("dt-alarms-region--system", cut.Markup);
        Assert.Contains("设备 1", cut.Markup);
        Assert.Contains("系统规则报警", cut.Markup);

        var devicePos = cut.Markup.IndexOf("dt-alarms-region--device", StringComparison.Ordinal);
        var systemPos = cut.Markup.IndexOf("dt-alarms-region--system", StringComparison.Ordinal);
        Assert.True(devicePos >= 0 && systemPos > devicePos, "设备报警区应排在系统规则区之前");

        Assert.Contains("dt-needs-owner-alarms--device", cut.Markup);
        Assert.Contains("打开工站配置", cut.Markup);
        // 有待接手设备报警时系统区默认折叠，避免淹没设备处置。
        Assert.Contains("已折叠系统规则区", cut.Markup);
        Assert.DoesNotContain("会话被抢占", cut.Markup);

        var toggle = cut.FindAll("button")
            .First(button => button.ClassList.Contains("dt-alarms-region-toggle"));
        toggle.Click();
        cut.WaitForAssertion(() => Assert.Contains("会话被抢占", cut.Markup));
    }

    [Fact]
    public void Device_summary_chip_filters_to_custom_device_kind()
    {
        var incidents = new FakeAlarmIncidents
        {
            Attention =
            [
                new AlarmIncidentDraft
                {
                    Id = 1, Key = "custom:23:1", Kind = LineAlarmKind.CustomDevice,
                    Message = "设备限值触发", RaisedAt = DateTime.Now
                },
                new AlarmIncidentDraft
                {
                    Id = 2, Key = "fault:23", Kind = LineAlarmKind.StationFault,
                    Message = "工站故障条目", RaisedAt = DateTime.Now
                }
            ]
        };
        Context.Services.AddSingleton<IAlarmIncidents>(incidents);
        Context.Services.AddSingleton<ILineAlarmBoard>(new LineAlarmBoard());
        var cut = RenderAlarms();
        cut.WaitForAssertion(() => Assert.Contains("设备限值触发", cut.Markup));

        // 设备区内「只看设备」把类型筛到 CustomDevice，系统区随之隐藏。
        cut.FindAll("button").First(button => button.TextContent.Contains("只看设备")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("设备限值触发", cut.Markup);
            Assert.DoesNotContain("dt-alarms-region--system", cut.Markup);
            Assert.DoesNotContain("工站故障条目", cut.Markup);
        });
    }

        private sealed class FakeAlarmIncidents : IAlarmIncidents
    {
        public List<AlarmIncidentDraft> Attention { get; set; } = [];

        public List<AlarmIncidentDraft> Recent { get; set; } = [];

        public List<long> Acknowledged { get; } = [];

        public string? AcknowledgedBy { get; private set; }

        public Task<IReadOnlyList<AlarmIncidentDraft>> ListAttentionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlarmIncidentDraft>>(Attention);

        public Task<IReadOnlyList<AlarmIncidentDraft>> ListRecentClosedAsync(int take, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlarmIncidentDraft>>(Recent.Take(take).ToList());

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
