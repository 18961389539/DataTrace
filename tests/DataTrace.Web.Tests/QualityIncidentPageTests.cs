using DataTrace.Application.Quality;
using DataTrace.Application.Alarms;
using DataTrace.Application.Identity;
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
    private readonly Mock<IUserAdministration> _users = new();
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
    public void New_incident_offers_business_identifier_source_search_without_internal_ids()
    {
        RegisterServices();
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidentNew>();

        Assert.Contains("第一步：选择源采集记录", cut.Markup);
        Assert.Contains("流水号", cut.Markup);
        Assert.Contains("托盘码", cut.Markup);
        Assert.Contains("工站", cut.Markup);
        Assert.Contains("开始日期", cut.Markup);
        Assert.Contains("结束日期", cut.Markup);
        Assert.DoesNotContain("采集记录 ID", cut.Markup);
        Assert.DoesNotContain("记录月份", cut.Markup);
        Assert.DoesNotContain("创建异常单", cut.Markup);
        Assert.NotNull(cut.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>());
        _collect.Verify(query => query.GetRecordAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Source_search_uses_business_filters_and_selection_loads_the_selected_record()
    {
        const string monthKey = "202610";
        const long recordId = 83;
        var source = new CollectRecord
        {
            Id = recordId,
            SerialNo = "SN-83",
            PalletCode = "PALLET-83",
            StationId = 2,
            StationCode = "ST020",
            TriggerTime = new DateTime(2026, 10, 8, 10, 0, 0),
            Judgement = Judgement.Ng
        };
        _collect.Setup(query => query.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult
            {
                Total = 1,
                Items = [new CollectRecordListItem { MonthKey = monthKey, Record = source }]
            });
        _collect.Setup(query => query.GetRecordAsync(monthKey, recordId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        RegisterServices();
        UseRole(AppRoles.Operator);
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidentNew>();
        TypeInto(cut, "流水号", "SN-83");
        TypeInto(cut, "托盘码", "PALLET-83");
        ClickButton(cut, "搜索记录");

        _collect.Verify(query => query.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.SerialNo == "SN-83"
                                                   && request.PalletCode == "PALLET-83"
                                                   && request.From < request.To
                                                   && request.Take == 50),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("SN-83", cut.Markup);
        ClickButton(cut, "选择此记录");
        Assert.Contains("已选择源记录：SN-83", cut.Markup);
        Assert.Contains("创建异常单", cut.Markup);
        _incidents.Verify(service => service.CreateAsync(
            It.IsAny<CreateQualityIncidentRequest>(), It.IsAny<QualityIncidentActor>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AppRoles.Operator, false)]
    [InlineData(AppRoles.Engineer, true)]
    public void Source_search_failure_shows_one_safe_inline_error_and_restricts_diagnostics(string role, bool canSeeDiagnostics)
    {
        const string internalFailure = "sqlite-path-secret";
        _role = role;
        _collect.Setup(query => query.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(internalFailure));
        RegisterServices();
        UseRole(role);
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidentNew>();
        ClickButton(cut, "搜索记录");

        Assert.Contains("源记录搜索暂时失败", cut.Markup);
        Assert.Single(cut.FindAll(".mud-alert"));
        Assert.DoesNotContain("搜索成功，但当前时间范围", cut.Markup);
        Assert.Equal(canSeeDiagnostics, cut.Markup.Contains(internalFailure, StringComparison.Ordinal));
        Assert.Empty(Toast.Messages);
    }

    [Fact]
    public void Incident_list_exposes_role_relevant_work_queues_and_guided_empty_state()
    {
        _role = AppRoles.Engineer;
        _incidents.Setup(service => service.ListAsync(It.IsAny<QualityIncidentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityIncidentListResult { Items = [], Total = 0 });
        RegisterServices();
        UseRole(AppRoles.Engineer);
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidents>();

        Assert.Contains("我的待办", cut.Markup);
        Assert.Contains("未接手", cut.Markup);
        Assert.Contains("待复核", cut.Markup);
        Assert.Contains("处置状态", cut.Markup);
        Assert.Contains("目前还没有质量异常单", cut.Markup);
        var queue = cut.Find("[role='group'][aria-label='质量异常工作队列']");
        Assert.Equal("true", queue.QuerySelectorAll("button").Single(button => button.TextContent.Contains("全部异常")).GetAttribute("aria-pressed"));
        Assert.Equal("false", queue.QuerySelectorAll("button").Single(button => button.TextContent.Contains("未接手")).GetAttribute("aria-pressed"));

        ClickButton(cut, "未接手");

        Assert.Equal("true", queue.QuerySelectorAll("button").Single(button => button.TextContent.Contains("未接手")).GetAttribute("aria-pressed"));
        Assert.Equal("false", queue.QuerySelectorAll("button").Single(button => button.TextContent.Contains("全部异常")).GetAttribute("aria-pressed"));
        Assert.Contains("选择记录并建单", cut.Markup);
    }

    [Fact]
    public void Incident_list_distinguishes_empty_data_from_filters_with_no_matches()
    {
        _incidents.Setup(service => service.ListAsync(It.IsAny<QualityIncidentQuery>(), It.IsAny<CancellationToken>()))
            .Returns((QualityIncidentQuery query, CancellationToken _) => Task.FromResult(
                new QualityIncidentListResult
                {
                    Items = [],
                    Total = query.Take == 1 && query.Status is null ? 3 : 0
                }));
        RegisterServices();
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidents>();

        Assert.Contains("系统中已有质量异常单，但“全部异常”队列", cut.Markup);
        Assert.Contains("清除筛选并查看全部", cut.Markup);
    }

    [Theory]
    [InlineData(AppRoles.Operator, false)]
    [InlineData(AppRoles.Engineer, true)]
    public void List_failure_uses_safe_inline_message_and_role_gated_diagnostics(string role, bool canSeeDiagnostics)
    {
        const string internalFailure = "database-connection-secret";
        _role = role;
        _incidents.Setup(service => service.ListAsync(It.IsAny<QualityIncidentQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(internalFailure));
        RegisterServices();
        UseRole(role);
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidents>();

        Assert.Contains("质量异常单暂时无法加载", cut.Markup);
        Assert.Equal(canSeeDiagnostics, cut.Markup.Contains(internalFailure, StringComparison.Ordinal));
        Assert.Empty(Toast.Messages);
    }

    [Fact]
    public void Unassigned_queue_excludes_incidents_with_an_existing_owner()
    {
        var unassigned = new QualityIncidentListItem
        {
            Id = 21,
            IncidentNumber = "QI-UNASSIGNED",
            Title = "待接手异常",
            StationCode = "ST020",
            RecipeCode = "",
            Status = QualityIncidentStatus.Open,
            Owner = null,
            CreatedAt = DateTime.Today,
            UpdatedAt = DateTime.Today
        };
        var assigned = new QualityIncidentListItem
        {
            Id = 22,
            IncidentNumber = "QI-ASSIGNED",
            Title = "已指派异常",
            StationCode = "ST020",
            RecipeCode = "",
            Status = QualityIncidentStatus.Open,
            Owner = "operator",
            CreatedAt = DateTime.Today,
            UpdatedAt = DateTime.Today
        };
        _incidents.Setup(service => service.ListAsync(It.IsAny<QualityIncidentQuery>(), It.IsAny<CancellationToken>()))
            .Returns((QualityIncidentQuery _, CancellationToken _) => Task.FromResult(
                new QualityIncidentListResult { Items = [unassigned, assigned], Total = 2 }));
        _role = AppRoles.Operator;
        RegisterServices();
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidents>();
        ClickButton(cut, "未接手");

        Assert.Contains("QI-UNASSIGNED", cut.Markup);
        Assert.DoesNotContain("QI-ASSIGNED", cut.Markup);
        _incidents.Verify(service => service.ListAsync(
            It.Is<QualityIncidentQuery>(query => query.Status == QualityIncidentStatus.Open
                                                 && query.Owner == null
                                                 && query.Take == 100),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Verification_record_can_be_found_and_selected_by_business_identifiers()
    {
        const long incidentId = 17;
        const string monthKey = "202610";
        const long recordId = 91;
        var details = new QualityIncidentDetails
        {
            Incident = new QualityIncident
            {
                Id = incidentId,
                IncidentNumber = "QI-20261008-000017",
                Title = "压力超限",
                Description = "验证选择器测试",
                Status = QualityIncidentStatus.InProgress,
                Owner = "admin",
                CreatedBy = "admin",
                CreatedAt = new DateTime(2026, 10, 8, 10, 0, 0),
                UpdatedAt = new DateTime(2026, 10, 8, 10, 5, 0),
                Version = 1
            },
            Evidence = [],
            Activities = []
        };
        var verification = new CollectRecord
        {
            Id = recordId,
            SerialNo = "VERIFY-91",
            PalletCode = "PALLET-91",
            StationId = 2,
            StationCode = "ST020",
            TriggerTime = new DateTime(2026, 10, 8, 10, 10, 0),
            Judgement = Judgement.Ok
        };
        _incidents.Setup(service => service.GetAsync(incidentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _collect.Setup(query => query.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult
            {
                Total = 1,
                Items = [new CollectRecordListItem { MonthKey = monthKey, Record = verification }]
            });
        _role = AppRoles.Operator;
        RegisterServices();
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidentDetail>(parameters => parameters.Add(page => page.Id, incidentId));

        Assert.Contains("搜索验证记录", cut.Markup);
        Assert.Contains("验证流水号", cut.Markup);
        Assert.Contains("托盘码", cut.Markup);
        Assert.DoesNotContain("验证记录 ID", cut.Markup);
        Assert.DoesNotContain("验证记录月份", cut.Markup);
        TypeInto(cut, "验证流水号", "VERIFY-91");
        TypeInto(cut, "托盘码", "PALLET-91");
        ClickButton(cut, "搜索验证记录");

        _collect.Verify(query => query.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.SerialNo == "VERIFY-91"
                                                   && request.PalletCode == "PALLET-91"
                                                   && request.Take == 50),
            It.IsAny<CancellationToken>()), Times.Once);
        ClickButton(cut, "选择");
        Assert.Contains("已选 VERIFY-91 · ST020", cut.Markup);
        Assert.NotNull(cut.FindComponent<Microsoft.AspNetCore.Components.Routing.NavigationLock>());
    }

    [Fact]
    public async Task Detail_action_failure_is_one_safe_inline_message_without_duplicate_toast()
    {
        const long incidentId = 31;
        const string internalFailure = "raw-provider-details";
        var details = new QualityIncidentDetails
        {
            Incident = new QualityIncident
            {
                Id = incidentId,
                IncidentNumber = "QI-20261008-000031",
                Title = "轴向尺寸异常",
                Description = "详情错误消息安全测试",
                Status = QualityIncidentStatus.InProgress,
                Owner = "admin",
                CreatedBy = "admin",
                CreatedAt = DateTime.Today,
                UpdatedAt = DateTime.Today,
                Version = 1
            },
            Evidence = [],
            Activities = []
        };
        _incidents.Setup(service => service.GetAsync(incidentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        _incidents.Setup(service => service.AddWorkNoteAsync(
                incidentId, It.IsAny<QualityIncidentActionCommand>(), It.IsAny<int>(),
                It.IsAny<QualityIncidentActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(internalFailure));
        _role = AppRoles.Operator;
        RegisterServices();
        RenderPopoverHost();

        var cut = Context.Render<QualityIncidentDetail>(parameters => parameters.Add(page => page.Id, incidentId));
        InputForLabel(cut, "本次处置措施").Input("检查治具");
        var button = cut.FindAll("button").Single(element => element.TextContent.Contains("记录措施"));
        Assert.False(button.HasAttribute("disabled"));
        await button.ClickAsync();

        _incidents.Verify(service => service.AddWorkNoteAsync(
            incidentId, It.IsAny<QualityIncidentActionCommand>(), It.IsAny<int>(),
            It.IsAny<QualityIncidentActor>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(internalFailure, cut.Markup);
        var operationAlerts = cut.FindAll(".mud-alert")
            .Where(alert => alert.TextContent.Contains("操作未完成") || alert.TextContent.Contains("操作暂时无法完成"))
            .ToList();
        Assert.Single(operationAlerts);
        Assert.Empty(Toast.Messages);
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
        Assert.Contains("搜索记录", cut.Markup);
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
        Context.Services.AddSingleton(_users.Object);
    }

    private void NavigateToNewIncident(string query)
        => Context.Services.GetRequiredService<NavigationManager>().NavigateTo($"/quality/incidents/new?{query}");

    private static QualityIncidentActor Actor(string role)
        => new("admin", [role]);
}
