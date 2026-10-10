using DataTrace.Application.Quality;
using DataTrace.Application.Alarms;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

public class QualityIncidentPageTests : WebTestBase
{
    private readonly Mock<IQualityIncidentService> _incidents = new();
    private readonly Mock<IQualityIncidentActorProvider> _actors = new();
    private readonly Mock<ICollectQuery> _collect = new();
    private readonly Mock<IAlarmIncidents> _alarmIncidents = new();
    private string _role = AppRoles.Operator;

    public QualityIncidentPageTests()
    {
        _incidents.Setup(service => service.ListAsync(It.IsAny<QualityIncidentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityIncidentListResult
            {
                Items =
                [
                    new QualityIncidentListItem
                    {
                        Id = 7,
                        IncidentNumber = "QI-20261008-000007",
                        Title = "压力超限",
                        StationCode = "ST020",
                        RecipeCode = "MODEL-A",
                        Severity = QualityIncidentSeverity.High,
                        Status = QualityIncidentStatus.InProgress,
                        Owner = "operator",
                        CreatedAt = new DateTime(2026, 10, 8, 10, 0, 0),
                        UpdatedAt = new DateTime(2026, 10, 8, 10, 5, 0),
                        Version = 2,
                        EvidenceCount = 3
                    }
                ],
                Total = 1
            });
        _actors.Setup(provider => provider.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Actor(_role)));
        _alarmIncidents.Setup(service => service.ListAttentionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlarmIncidentDraft>());
        _alarmIncidents.Setup(service => service.ListRecentClosedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AlarmIncidentDraft>());
    }

    [Theory]
    [InlineData(AppRoles.Operator, true)]
    [InlineData(AppRoles.Viewer, false)]
    public void Create_entry_is_visible_only_to_roles_allowed_to_work(string role, bool visible)
    {
        _role = role;
        RegisterServices();
        UseRole(role);
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidents>();

        Assert.Equal(visible, cut.FindAll("a[href='/quality/incidents/new']").Count == 1);
        Assert.Contains("QI-20261008-000007", cut.Markup);
        Assert.Contains("压力超限", cut.Markup);
    }

    [Fact]
    public void New_incident_requires_a_valid_source_record()
    {
        RegisterServices();
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidentNew>();

        Assert.Contains("请从采集记录明细进入", cut.Markup);
        Assert.DoesNotContain("创建异常单", cut.Markup);
        _collect.Verify(query => query.GetRecordAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Viewer_with_a_valid_source_record_gets_read_only_access()
    {
        const string monthKey = "202610";
        const long recordId = 82;
        _role = AppRoles.Viewer;
        RegisterServices();
        UseRole(_role);
        RenderPopoverHost();
        _collect.Setup(query => query.GetRecordAsync(monthKey, recordId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectRecord
            {
                Id = recordId,
                SerialNo = "20261008-000082",
                StationCode = "ST020",
                TriggerTime = new DateTime(2026, 10, 8, 10, 0, 0)
            });

            NavigateToNewIncident($"month={monthKey}&recordId={recordId}");
            var cut = Context.Render<QualityIncidentNew>();

        Assert.Contains("只有查看权限", cut.Markup);
        Assert.DoesNotContain("创建异常单", cut.Markup);
        _incidents.Verify(service => service.CreateAsync(It.IsAny<CreateQualityIncidentRequest>(), It.IsAny<QualityIncidentActor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Consecutive_ng_alarm_can_be_carried_into_a_new_incident()
    {
        const long alarmId = 93;
        _alarmIncidents.Setup(service => service.ListAttentionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new AlarmIncidentDraft
                {
                    Id = alarmId,
                    Key = "ng:2",
                    Kind = LineAlarmKind.ConsecutiveNg,
                    Message = "ST020 连续 3 件 NG",
                    RaisedAt = new DateTime(2026, 10, 8, 9, 55, 0)
                }
            ]);
        RegisterServices();
        UseRole(AppRoles.Operator);
        RenderPopoverHost();

        NavigateToNewIncident($"alarmId={alarmId}");
        var cut = Context.Render<QualityIncidentNew>();

        Assert.Contains("关联质量报警", cut.Markup);
        Assert.Contains("ST020 连续 3 件 NG", cut.Markup);
        Assert.Contains("载入源记录", cut.Markup);
    }

    [Fact]
    public void Create_uses_source_evidence_and_rejects_a_partial_scope()
    {
        const string monthKey = "202610";
        const long recordId = 81;
        _role = AppRoles.Operator;
        RegisterServices();
        UseRole(_role);
        RenderPopoverHost();
        var source = new CollectRecord
        {
            Id = recordId,
            SerialNo = "20261008-000081",
            PalletCode = "P0081",
            StationId = 2,
            StationCode = "ST020",
            RecipeCode = "MODEL-A",
            TriggerTime = new DateTime(2026, 10, 8, 10, 0, 0),
            Judgement = Judgement.Ng
        };
        _collect.Setup(query => query.GetRecordAsync(monthKey, recordId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        _incidents.Setup(service => service.CreateAsync(It.IsAny<CreateQualityIncidentRequest>(), It.IsAny<QualityIncidentActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(42);

        NavigateToNewIncident($"month={monthKey}&recordId={recordId}");
        var cut = Context.Render<QualityIncidentNew>();
        TypeInto(cut, "异常标题", "ST020压力超限");
        TypeInto(cut, "异常描述", "记录详情创建的处置单");
        TypeInto(cut, "开始时间", "2026-10-08 09:00");
        cut.Find("form").TriggerEvent("onsubmit", new EventArgs());

        Assert.Contains("必须同时填写开始和结束时间", cut.Markup);
        _incidents.Verify(service => service.CreateAsync(It.IsAny<CreateQualityIncidentRequest>(), It.IsAny<QualityIncidentActor>(), It.IsAny<CancellationToken>()), Times.Never);

        TypeInto(cut, "结束时间", "2026-10-08 11:00");
        cut.Find("form").TriggerEvent("onsubmit", new EventArgs());

        _incidents.Verify(service => service.CreateAsync(
            It.Is<CreateQualityIncidentRequest>(request => request.MonthKey == monthKey
                                                             && request.RecordId == recordId
                                                             && request.StationId == source.StationId
                                                             && request.ScopeFrom == new DateTime(2026, 10, 8, 9, 0, 0)
                                                             && request.ScopeTo == new DateTime(2026, 10, 8, 11, 0, 0)
                                                             && request.RequestId != Guid.Empty),
            It.Is<QualityIncidentActor>(actor => actor.UserName == "admin"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private void RegisterServices()
    {
        Context.Services.AddSingleton(_incidents.Object);
        Context.Services.AddSingleton(_actors.Object);
        Context.Services.AddSingleton(_collect.Object);
        Context.Services.AddSingleton(_alarmIncidents.Object);
    }

    private void NavigateToNewIncident(string query)
        => Context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/quality/incidents/new?{query}");

    private static QualityIncidentActor Actor(string role)
        => new("admin", [role]);
}