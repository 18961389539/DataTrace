using DataTrace.Application.Alarms;
using DataTrace.Domain.Constants;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

public class AlarmsPageTests : WebTestBase
{
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

        var cut = Context.RenderComponent<Alarms>();
        cut.WaitForAssertion(() => Assert.Contains("待接手", cut.Markup));
        ClickButton(cut, "接手");

        cut.WaitForAssertion(() => Assert.Contains("已接手，尚未解除", cut.Markup));
        Assert.Equal(7, Assert.Single(incidents.Acknowledged));
        Assert.Equal("admin", incidents.AcknowledgedBy);
        Assert.Contains(Toast.Messages, message => message.Contains("已接手"));
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

        var cut = Context.RenderComponent<Alarms>();

        cut.WaitForAssertion(() => Assert.Contains("只能查看，不能接手", cut.Markup));
        Assert.DoesNotContain("接手</button>", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(incidents.Acknowledged);
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
