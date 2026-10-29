using System.Security.Claims;
using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Components.Shared;
using DataTrace.Web.Services;
using DataTrace.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 数据查询页的导出闸门与留痕：按钮只给能导出的角色；真的导出时必须写审计，
/// 而审计写不进去不能报成"导出失败"——文件已经在用户手里了。
/// </summary>
public class QueryPageTests : WebTestBase
{
    private readonly Mock<IRuntimeStore> _store = new();
    private readonly Mock<IAuditLogger> _audit = new();
    private string _role = AppRoles.Administrator;

    public QueryPageTests()
    {
        _store
            .Setup(s => s.ListRecipeCodesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _store
            .Setup(s => s.QuerySessionsAsync(It.IsAny<SessionQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionQueryResult { Total = 0, Items = Array.Empty<SessionListItem>() });
        _store
            .Setup(s => s.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult
            {
                Total = 1,
                Items =
                [
                    new CollectRecordListItem
                    {
                        MonthKey = "202609",
                        Record = new CollectRecord
                        {
                            Id = 1,
                            SerialNo = "20260919-000001",
                            PalletCode = "P0001",
                            StationCode = "ST010",
                            TriggerTime = new DateTime(2026, 9, 19, 8, 0, 0),
                            Judgement = Judgement.Ok
                        }
                    }
                ]
            });
    }

    private IRenderedComponent<Query> Render(string? initialUri = null)
    {
        Context.Services.AddSingleton(_store.Object);
        Context.Services.AddSingleton(_audit.Object);
        Context.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthenticationStateProvider(_role));
        if (initialUri is not null)
            Context.Services.GetRequiredService<NavigationManager>().NavigateTo(initialUri);
        // MudSelect 的浮层要一个 provider：真实应用由 MainLayout 提供。
        Context.Render<MudPopoverProvider>();

        return Context.Render<Query>();
    }

    /// <summary>
    /// 按下拉里的选项切换型号筛选。参数是筛选状态：null=全部型号、""=未选型号、其它=型号编码。
    /// 从真正渲染出来的选项里取取值，顺带钉住"这个选项确实在下拉里"；
    /// 返回选中的取值，供用例比对页面回读的那一份（两者不相等，MudSelect 就认不出选中项）。
    /// </summary>
    private static async Task<RecipeFilterValue> SelectRecipeAsync(IRenderedComponent<Query> cut, string? recipe)
    {
        var select = cut.FindComponent<MudSelect<RecipeFilterValue>>();
        var value = cut.FindComponents<MudSelectItem<RecipeFilterValue>>()
            .Select(i => i.Instance.Value)
            .First(v => v is not null && v.Recipe == recipe);
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
        return value!;
    }

    [Fact]
    public async Task Query_primary_inputs_expandable_filters_and_accessible_sort_state_are_rendered()
    {
        var cut = Render();

        var primary = cut.Find(".dt-query-primary");
        Assert.Contains("快速查询", primary.TextContent);
        Assert.Contains("流水号", primary.TextContent);
        Assert.NotNull(primary.QuerySelector("input[placeholder='扫码或输入托盘码']"));
        Assert.Equal("submit", primary.QuerySelector("button")?.GetAttribute("type"));
        Assert.NotNull(cut.FindComponent<QueryPresetsPanel>());

        var modeButtons = cut.FindAll("[aria-label='查询模式'] button");
        Assert.Equal("true", modeButtons.Single(button => button.TextContent.Contains("记录查询")).GetAttribute("aria-pressed"));
        Assert.Equal("false", modeButtons.Single(button => button.TextContent.Contains("产品追溯")).GetAttribute("aria-pressed"));

        var toggle = cut.Find(".dt-query-advanced-toggle");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("dt-query-advanced-fields", toggle.GetAttribute("aria-controls"));
        Assert.Equal("descending", cut.Find("th[aria-sort='descending']").GetAttribute("aria-sort"));
        Assert.Equal("none", cut.Find("th[aria-sort='none']").GetAttribute("aria-sort"));

        await toggle.ClickAsync();

        Assert.Equal("true", cut.Find(".dt-query-advanced-toggle").GetAttribute("aria-expanded"));
        Assert.True(cut.Find("#dt-query-advanced-fields").ClassList.Contains("is-expanded"));

        ClickButton(cut, "产品追溯");
        modeButtons = cut.FindAll("[aria-label='查询模式'] button");
        Assert.Equal("false", modeButtons.Single(button => button.TextContent.Contains("记录查询")).GetAttribute("aria-pressed"));
        Assert.Equal("true", modeButtons.Single(button => button.TextContent.Contains("产品追溯")).GetAttribute("aria-pressed"));

        ClickButton(cut, "记录查询");
        modeButtons = cut.FindAll("[aria-label='查询模式'] button");
        Assert.Equal("true", modeButtons.Single(button => button.TextContent.Contains("记录查询")).GetAttribute("aria-pressed"));
        Assert.Equal("false", modeButtons.Single(button => button.TextContent.Contains("产品追溯")).GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Query_filter_summary_and_sort_semantics_round_trip_from_the_url()
    {
        var cut = Render("/query?serial=SERIAL-29&pallet=PALLET-29&station=23&judge=Ng&result=11&sort=pallet&dir=asc");
        var nav = Context.Services.GetRequiredService<NavigationManager>();

        Assert.Contains("当前条件", cut.Markup);
        Assert.Contains("流水号 SERIAL-29", cut.Markup);
        Assert.Contains("托盘码 PALLET-29", cut.Markup);
        Assert.Contains("工站 23", cut.Markup);
        Assert.Contains("sort=pallet", nav.Uri);
        Assert.Contains("serial=SERIAL-29", nav.Uri);
        Assert.Equal("ascending", cut.Find("th[aria-sort='ascending']").GetAttribute("aria-sort"));
        Assert.Contains("SERIAL-29", cut.Markup);
    }

    [Fact]
    public void Saved_shift_scheme_recalculates_dates_and_preserves_filters()
    {
        var preset = new QueryPreset("夜班 NG", "query?from=2026-09-01&to=2026-09-01&station=2&judge=Ng&recipe=NEW&alias=OLD&sort=duration&dir=asc&page=4&size=50", "shift");
        var url = preset.Resolve(new DateTime(2026, 10, 11, 1, 0, 0), 20, 12);
        var request = RecordListNavigation.ReadRequest(url, 0, 50);

        Assert.Equal(new DateTime(2026, 10, 10, 20, 0, 0), request.From);
        Assert.Equal(new DateTime(2026, 10, 11, 8, 0, 0).AddTicks(-1), request.To);
        Assert.Equal(2, request.StationId);
        Assert.Equal(Judgement.Ng, request.Judgement);
        Assert.Equal(new[] { "NEW", "OLD" }, request.RecipeCodes);
        Assert.Equal(CollectSortField.DurationMs, request.SortBy);
        Assert.False(request.SortDescending);
        Assert.DoesNotContain("page=", url);
        Assert.Contains("size=50", url);
    }

    [Fact]
    public void Saved_rolling_week_uses_the_day_of_invocation()
    {
        var url = new QueryPreset("近七天", "query", "week").Resolve(new DateTime(2026, 10, 11), 20, 12);
        var request = RecordListNavigation.ReadRequest(url, 0, 20);
        Assert.Equal(new DateTime(2026, 10, 5), request.From);
        Assert.Equal(new DateTime(2026, 10, 12).AddTicks(-1), request.To);
    }

    [Fact]
    public async Task Applying_a_scheme_queries_its_filters_and_sort()
    {
        var cut = Render();
        await cut.InvokeAsync(() => cut.FindComponent<QueryPresetsPanel>().Instance.Apply.InvokeAsync(
            "query?station=2&judge=Ng&recipe=NEW&alias=OLD&sort=duration&dir=asc&size=50&from=2026-10-01&to=2026-10-03"));
        _store.Verify(store => store.QueryAsync(It.Is<CollectQueryRequest>(request => request.StationId == 2
            && request.Judgement == Judgement.Ng && request.SortBy == CollectSortField.DurationMs
            && !request.SortDescending && request.Take == 50 && request.RecipeCodes!.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scheme_save_persists_to_account_store_and_mirrors_browser_cache()
    {
        Context.JSInterop.Setup<bool>("dtQueryPresets.set", _ => true).SetResult(true);
        var cut = Render();
        Assert.Equal("admin", Assert.Single(Context.JSInterop.Invocations["dtQueryPresets.get"]).Arguments[0]);
        ClickButton(cut, "保存查询方案");
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "方案名称")
            .Instance.ValueChanged.InvokeAsync("每日 NG"));
        ClickButton(cut, "保存方案");

        var stored = await Context.Services.GetRequiredService<IQueryPresetStore>().ListAsync("admin");
        var preset = Assert.Single(stored);
        Assert.Equal("每日 NG", preset.Name);
        Assert.Equal("today", preset.Range);
        Assert.Contains(Toast.Messages, message => message.Contains("查询方案已保存"));

        // 服务器保存成功后会顺带镜像到本机，方便旧逻辑/离线兜底。
        Assert.Contains(Context.JSInterop.Invocations, invocation => invocation.Identifier == "dtQueryPresets.set");
    }

    [Fact]
    public async Task Scheme_overwrite_can_be_cancelled_without_storage_write()
    {
        Context.JSInterop.Setup<QueryPreset[]?>("dtQueryPresets.get", _ => true)
            .SetResult([new QueryPreset("每日 NG", "query?judge=Ng", "today")]);
        Context.JSInterop.Setup<bool>("dtQueryPresets.set", _ => true).SetResult(true);
        Dialogs.MessageBoxResult = false;
        var cut = Render();
        // 首次加载会把本机方案迁到服务器，可能已经写过一次；之后取消覆盖不应再写。
        var setsBefore = Context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "dtQueryPresets.set");
        var store = Context.Services.GetRequiredService<IQueryPresetStore>();
        var before = await store.ListAsync("admin");
        ClickButton(cut, "保存查询方案");
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "方案名称")
            .Instance.ValueChanged.InvokeAsync("每日 NG"));
        ClickButton(cut, "保存方案");
        Assert.Single(Dialogs.MessageBoxes);
        Assert.Equal(setsBefore, Context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "dtQueryPresets.set"));
        var after = await store.ListAsync("admin");
        Assert.Equal(before.Count, after.Count);
        Assert.Equal(before.Single().Url, after.Single().Url);
    }

    [Theory]
    [InlineData(AppRoles.Administrator, true)]
    [InlineData(AppRoles.Engineer, true)]
    [InlineData(AppRoles.Operator, false)]
    [InlineData(AppRoles.Viewer, false)]
    public void Export_button_is_only_rendered_for_roles_that_may_export(string role, bool visible)
    {
        _role = role;

        var cut = Render();

        Assert.Equal(visible, cut.FindAll("button").Any(b => b.TextContent.Contains("导出 CSV")));
    }

    [Fact]
    public async Task Search_controls_remain_enabled_but_export_is_disabled_while_query_is_loading()
    {
        var query = new TaskCompletionSource<CollectQueryResult>();
        _store
            .Setup(s => s.QuerySessionsAsync(It.IsAny<SessionQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionQueryResult { Total = 0, Items = Array.Empty<SessionListItem>() });
        _store
            .Setup(s => s.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .Returns(query.Task);

        var cut = Render();

        var export = cut.FindAll("button").Single(button => button.TextContent.Contains("导出 CSV"));
        Assert.True(export.HasAttribute("disabled"));
        Assert.False(cut.FindComponent<DateRangeFilter>().Instance.Disabled);
        Assert.All(cut.FindComponents<MudTextField<string>>(), field => Assert.False(field.Instance.Disabled));

        await cut.InvokeAsync(() => query.SetResult(new CollectQueryResult
        {
            Total = 1,
            Items =
            [
                new CollectRecordListItem
                {
                    MonthKey = "202609",
                    Record = new CollectRecord
                    {
                        Id = 1,
                        SerialNo = "20260919-000001",
                        PalletCode = "P0001",
                        StationCode = "ST010",
                        TriggerTime = new DateTime(2026, 9, 19, 8, 0, 0),
                        Judgement = Judgement.Ok
                    }
                }
            ]
        }));
        cut.WaitForAssertion(() => Assert.False(cut.FindAll("button")
            .Single(button => button.TextContent.Contains("导出 CSV")).HasAttribute("disabled")));
    }

    [Fact]
    public async Task Input_during_a_pending_query_is_used_by_the_next_query()
    {
        var pending = new TaskCompletionSource<CollectQueryResult>();
        _store.SetupSequence(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .Returns(pending.Task)
            .ReturnsAsync(new CollectQueryResult { Total = 0, Items = [] });
        var cut = Render();
        var input = cut.FindComponents<MudTextField<string>>().Single(field => field.Instance.Label == "托盘码");

        await cut.InvokeAsync(() => input.Instance.ValueChanged.InvokeAsync("SCAN-002"));
        Assert.False(input.Instance.Disabled);
        await cut.InvokeAsync(() => pending.SetResult(new CollectQueryResult { Total = 0, Items = [] }));

        cut.WaitForAssertion(() => _store.Verify(store => store.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.PalletCode == "SCAN-002"),
            It.IsAny<CancellationToken>()), Times.Once));
    }

    [Fact]
    public async Task Superseded_query_results_are_not_displayed_while_the_latest_query_is_pending()
    {
        var oldQuery = new TaskCompletionSource<CollectQueryResult>();
        var latestQuery = new TaskCompletionSource<CollectQueryResult>();
        _store.SetupSequence(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .Returns(oldQuery.Task)
            .Returns(latestQuery.Task);
        var cut = Render();
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "托盘码").Instance.ValueChanged.InvokeAsync("LATEST"));
        await cut.InvokeAsync(() => oldQuery.SetResult(new CollectQueryResult
        {
            Total = 1,
            Items = [new CollectRecordListItem { MonthKey = "202610", Record = new CollectRecord { PalletCode = "OBSOLETE" } }]
        }));

        cut.WaitForAssertion(() => _store.Verify(store => store.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.PalletCode == "LATEST"), It.IsAny<CancellationToken>()), Times.Once));
        Assert.DoesNotContain("OBSOLETE", cut.Markup);
        await cut.InvokeAsync(() => latestQuery.SetResult(new CollectQueryResult { Total = 0, Items = [] }));
    }

    [Fact]
    public async Task Failure_of_a_superseded_query_does_not_discard_the_latest_search()
    {
        var oldQuery = new TaskCompletionSource<CollectQueryResult>();
        _store.SetupSequence(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .Returns(oldQuery.Task)
            .ReturnsAsync(new CollectQueryResult { Total = 0, Items = [] });
        var cut = Render();
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>()
            .Single(field => field.Instance.Label == "托盘码").Instance.ValueChanged.InvokeAsync("LATEST"));
        await cut.InvokeAsync(() => oldQuery.SetException(new InvalidOperationException("old request")));

        cut.WaitForAssertion(() => _store.Verify(store => store.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.PalletCode == "LATEST"), It.IsAny<CancellationToken>()), Times.Once));
        Assert.DoesNotContain("查询失败", cut.Markup);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Superseded_last_page_correction_preserves_the_latest_search(bool fails)
    {
        var correction = new TaskCompletionSource<CollectQueryResult>();
        var latest = new TaskCompletionSource<CollectQueryResult>();
        _store.SetupSequence(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult { Total = 41, Items = [] })
            .ReturnsAsync(new CollectQueryResult { Total = 1, Items = [] })
            .Returns(correction.Task)
            .Returns(latest.Task);
        var cut = Render();
        var paging = cut.InvokeAsync(() => cut.FindComponent<MudPagination>().Instance.SelectedChanged.InvokeAsync(3));
        cut.WaitForAssertion(() => _store.Verify(store => store.QueryAsync(
            It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3)));
        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<int>>().First(s => s.Instance.MultiSelection).Instance.SelectedValuesChanged.InvokeAsync(new HashSet<int> { 23 }));

        if (fails)
            await cut.InvokeAsync(() => correction.SetException(new InvalidOperationException("old correction")));
        else
            await cut.InvokeAsync(() => correction.SetResult(new CollectQueryResult
            {
                Total = 1,
                Items = [new CollectRecordListItem { MonthKey = "202610", Record = new CollectRecord { PalletCode = "OBSOLETE" } }]
            }));

        cut.WaitForAssertion(() => _store.Verify(store => store.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.StationId == 23 && request.Skip == 0),
            It.IsAny<CancellationToken>()), Times.Once));
        Assert.DoesNotContain("OBSOLETE", cut.Markup);
        Assert.DoesNotContain("查询失败", cut.Markup);
        await cut.InvokeAsync(() => latest.SetResult(new CollectQueryResult { Total = 0, Items = [] }));
        await paging;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_last_page_correction_updates_the_total_or_reports_its_failure(bool fails)
    {
        var sequence = _store.SetupSequence(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult { Total = 41, Items = [] })
            .ReturnsAsync(new CollectQueryResult { Total = 1, Items = [] });
        if (fails)
            sequence.ThrowsAsync(new InvalidOperationException("current correction"));
        else
            sequence.ReturnsAsync(new CollectQueryResult
            {
                Total = 2,
                Items = [new CollectRecordListItem { MonthKey = "202610", Record = new CollectRecord { PalletCode = "CORRECTED" } }]
            });
        var cut = Render();

        await cut.InvokeAsync(() => cut.FindComponent<MudPagination>().Instance.SelectedChanged.InvokeAsync(3));

        if (fails)
        {
            Assert.Contains("查询失败", cut.Markup);
            Assert.DoesNotContain("CORRECTED", cut.Markup);
        }
        else
        {
            Assert.Contains("CORRECTED", cut.Markup);
            Assert.Contains("共 2 条", cut.Markup);
            Assert.Equal(1, cut.FindComponent<MudPagination>().Instance.Selected);
        }
        _store.Verify(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public void Session_deep_link_loads_the_exact_session_without_manual_serial_input()
    {
        _store.Setup(store => store.FindSessionTracesBySessionIdAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Trace("20260919-000001", Judgement.Ng)]);
        Context.Services.AddSingleton(_store.Object);
        RenderPopoverHost();

        var cut = Context.Render<ProductTraceabilityPanel>(parameters => parameters.Add(panel => panel.InitialSessionId, 7));

        _store.Verify(store => store.FindSessionTracesBySessionIdAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("20260919-000001", cut.Markup);
        Assert.Contains("首因", cut.Markup);
    }

    [Fact]
    public void Shift_shortcut_queries_exact_configured_shift_bounds()
    {
        var cut = Render();
        var settings = Config.Snapshot.Settings;
        var shift = ShiftWindow.Containing(DateTime.Now, settings.ShiftStartHour, settings.ShiftLengthHours);

        ClickButton(cut, "本班次");

        _store.Verify(store => store.QueryAsync(It.Is<CollectQueryRequest>(request => request.From == shift.Start
            && request.To == shift.End.AddTicks(-1)), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("from=", Context.Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Contains("to=", Context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Navigation_context_preserves_exact_times_sort_and_recipe_aliases()
    {
        var request = RecordListNavigation.ReadRequest(
            "query?from=2026-10-09T20:00:00&to=2026-10-10T07:59:59.9999999&station=23&judge=Ng&recipe=NEW&alias=OLD&sort=pallet&dir=asc", 19, 3);
        Assert.Equal(new DateTime(2026, 10, 9, 20, 0, 0), request.From);
        Assert.Equal(new DateTime(2026, 10, 10, 8, 0, 0).AddTicks(-1), request.To);
        Assert.Equal(23, request.StationId);
        Assert.Equal(Judgement.Ng, request.Judgement);
        Assert.Equal(new[] { "NEW", "OLD" }, request.RecipeCodes);
        Assert.Equal(CollectSortField.PalletCode, request.SortBy);
        Assert.False(request.SortDescending);
    }

    [Fact]
    public void Navigation_across_a_page_boundary_updates_the_return_page()
    {
        var item = new CollectRecordListItem { MonthKey = "202610", Record = new CollectRecord { Id = 23 } };
        var url = RecordListNavigation.DetailUrl(item, "query?station=23&sort=pallet&dir=asc", 20);
        Assert.StartsWith("/query/202610/23?", url);
        Assert.Contains("page%3D2", url);
        Assert.Contains("position=20", url);
        Assert.Contains("station%3D23", url);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_detail_navigation_checks_the_list_before_moving_across_pages(bool listChanged)
    {
        var records = Enumerable.Range(21, 3).Select(id => new CollectRecordListItem
        {
            MonthKey = "202610", Record = new CollectRecord { Id = id, PalletCode = "P" + id }
        }).ToList();
        _store.Setup(store => store.GetRecordAsync("202610", 22, It.IsAny<CancellationToken>())).ReturnsAsync(records[1].Record);
        _store.Setup(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult { Total = 50, Items = records });
        if (listChanged)
            records[1] = new CollectRecordListItem { MonthKey = "202610", Record = new CollectRecord { Id = 99 } };
        Context.Services.AddSingleton(_store.Object);
        Context.Services.AddSingleton(new Mock<ICurveFileStore>().Object);
        Context.Services.AddSingleton(new Mock<ICollectArchiveStore>().Object);
        RenderPopoverHost();
        Context.Services.GetRequiredService<NavigationManager>().NavigateTo("/query/202610/22?back=query%3Fstation%3D23%26sort%3Dpallet%26dir%3Dasc&position=19");

        var cut = Context.Render<RecordDetail>(parameters => parameters.Add(page => page.MonthKey, "202610").Add(page => page.RecordId, 22));

        if (listChanged)
        {
            Assert.Contains("列表已变化", cut.Find(".mud-alert").TextContent);
            Assert.True(cut.FindAll("button").Single(button => button.TextContent.Contains("下一条")).HasAttribute("disabled"));
        }
        else
        {
            var next = cut.FindAll("a").Single(link => link.TextContent.Contains("下一条"));
            Assert.Contains("/query/202610/23", next.GetAttribute("href"));
            Assert.Contains("page%3D2", next.GetAttribute("href"));
        }
        _store.Verify(store => store.QueryAsync(It.Is<CollectQueryRequest>(request => request.Skip == 18 && request.Take == 3
            && request.StationId == 23 && request.SortBy == CollectSortField.PalletCode && !request.SortDescending), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Trace_mode_keeps_record_search_separate_and_requests_an_exact_serial()
    {
        const string serial = "20260919-000001";
        _store
            .Setup(s => s.FindSessionTracesBySerialNoAsync(serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<CollectSessionTrace>());
        var cut = Render();

        ClickButton(cut, "产品追溯");
        TypeInto(cut, "产品流水号", serial);
        cut.Find("form").TriggerEvent("onsubmit", new EventArgs());

        _store.Verify(
            s => s.FindSessionTracesBySerialNoAsync(serial, It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Contains("没有找到完全匹配的流水号", cut.Markup);
        Assert.DoesNotContain("托盘码", cut.Markup);
    }

    /// <summary>
    /// 会话判 NG 时必须把首因一起摆出来：只有一个"不合格"，
    /// 操作工得逐站翻履历才知道从哪一站开始坏 —— 多站多件的会话一眼看不出来。
    /// </summary>
    [Fact]
    public void Trace_mode_shows_the_first_ng_station_and_reason()
    {
        const string serial = "20260919-000001";
        _store
            .Setup(s => s.FindSessionTracesBySerialNoAsync(serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Trace(serial, Judgement.Ng)]);
        var cut = Render();

        ClickButton(cut, "产品追溯");
        TypeInto(cut, "产品流水号", serial);
        cut.Find("form").TriggerEvent("onsubmit", new EventArgs());

        var firstNg = cut.Find(".dt-trace-first-ng");
        Assert.Contains("首因", firstNg.TextContent);
        Assert.Contains("ST020", firstNg.TextContent);
        Assert.Contains("质量不合格", firstNg.TextContent);
        Assert.Contains("压力超限", firstNg.TextContent);
    }

    /// <summary>全绿会话没有首因，那一行不能挂着"未知工站"之类的空壳。</summary>
    [Fact]
    public void Trace_mode_leaves_the_cause_line_out_for_qualified_sessions()
    {
        const string serial = "20260919-000001";
        _store
            .Setup(s => s.FindSessionTracesBySerialNoAsync(serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Trace(serial, Judgement.Ok)]);
        var cut = Render();

        ClickButton(cut, "产品追溯");
        TypeInto(cut, "产品流水号", serial);
        cut.Find("form").TriggerEvent("onsubmit", new EventArgs());

        Assert.Contains(serial, cut.Markup);
        Assert.Empty(cut.FindAll(".dt-trace-first-ng"));
    }

    [Theory]
    [InlineData(Judgement.Ok)]
    [InlineData(Judgement.Ng)]
    public void Trace_separates_route_coverage_from_historical_judgement_coverage(Judgement finalJudgement)
    {
        const string serial = "20260919-000001";
        var stations = Enumerable.Range(1, 9)
            .Select(id => new Station { Id = id, Code = $"ST{id:000}", Name = $"工站{id}", Sequence = id, Enabled = true })
            .ToList();
        Config.Snapshot = new AppConfigurationSnapshot { Stations = stations };
        var start = new DateTime(2026, 9, 19, 10, 0, 0);
        var records = stations.Select((station, index) =>
        {
            var judged = index < 2;
            var judgement = !judged
                ? Judgement.None
                : finalJudgement == Judgement.Ng && index == 0 ? Judgement.Ng : Judgement.Ok;
            return new CollectRecord
            {
                Id = index + 1,
                PalletSessionId = 7,
                SerialNo = serial,
                StationId = station.Id,
                StationCode = station.Code,
                TriggerTime = start.AddSeconds(index),
                CompleteTime = start.AddSeconds(index + 1),
                ResultCode = ResultCodes.Success,
                Judgement = judgement,
                TagValues =
                [
                    new TagValue
                    {
                        TagName = $"点位{station.Code}",
                        NumericValue = 5,
                        LowerLimit = judged ? 0 : null,
                        UpperLimit = judged ? 10 : null
                    }
                ]
            };
        }).ToList();
        var trace = new CollectSessionTrace
        {
            MonthKey = "202609",
            SessionId = 7,
            Session = new PalletSession
            {
                Id = 7,
                SerialNo = serial,
                StartTime = start,
                EndTime = start.AddMinutes(1),
                Status = SessionStatus.Closed,
                Judgement = finalJudgement
            },
            Records = records
        };

        var cut = RenderTrace(trace, serial);

        Assert.Contains("路线/采集覆盖 9/9 站", cut.Markup);
        Assert.Contains("判定适用 2 站 · 仅采集/未判定 7 站 · 缺记录 0 站", cut.Markup);
        Assert.Contains(DisplayLabels.JudgementText(finalJudgement), cut.Markup);
        Assert.Equal(finalJudgement == Judgement.Ok, cut.Markup.Contains("最终 OK 仅表示已配置的判定项通过"));
        Assert.Contains("未判定", cut.Markup);
    }

    [Fact]
    public void Trace_reports_missing_judgement_data_separately_from_collection_only()
    {
        const string serial = "20260919-000001";
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Stations =
            [
                new Station { Id = 10, Code = "ST010", Name = "检测站", Sequence = 1, Enabled = true },
                new Station { Id = 20, Code = "ST020", Name = "采集站", Sequence = 2, Enabled = true }
            ]
        };
        var trace = new CollectSessionTrace
        {
            MonthKey = "202609",
            SessionId = 7,
            Session = new PalletSession { Id = 7, SerialNo = serial, Judgement = Judgement.Ng },
            Records =
            [
                new CollectRecord
                {
                    Id = 11,
                    SerialNo = serial,
                    StationId = 10,
                    StationCode = "ST010",
                    ResultCode = ResultCodes.Success,
                    Judgement = Judgement.Ng,
                    TagValues = [new TagValue { TagName = "压力", LowerLimit = 0, UpperLimit = 10, NumericValue = null }]
                },
                new CollectRecord
                {
                    Id = 12,
                    SerialNo = serial,
                    StationId = 20,
                    StationCode = "ST020",
                    ResultCode = ResultCodes.Success,
                    Judgement = Judgement.None,
                    TagValues = [new TagValue { TagName = "配方号", NumericValue = 1 }]
                }
            ]
        };

        var cut = RenderTrace(trace, serial);

        Assert.Contains("判定适用 1 站 · 仅采集/未判定 1 站 · 缺记录 0 站", cut.Markup);
        Assert.Contains("判定数据缺失 1 项", cut.Markup);
    }

    [Fact]
    public void Record_detail_names_normal_unconfigured_and_missing_tag_judgements()
    {
        var record = new CollectRecord
        {
            Id = 22,
            SerialNo = "20260919-000001",
            PalletCode = "P0001",
            StationCode = "ST010",
            ResultCode = ResultCodes.Success,
            Judgement = Judgement.Ok,
            TagValues =
            [
                new TagValue { TagName = "压力", NumericValue = 5, LowerLimit = 0, UpperLimit = 10 },
                new TagValue { TagName = "配方号", NumericValue = 1 },
                new TagValue { TagName = "力值", NumericValue = null, LowerLimit = 0, UpperLimit = 10 }
            ]
        };
        _store.Setup(store => store.GetRecordAsync("202610", 22, It.IsAny<CancellationToken>())).ReturnsAsync(record);
        Context.Services.AddSingleton(_store.Object);
        Context.Services.AddSingleton(new Mock<ICurveFileStore>().Object);
        Context.Services.AddSingleton(new Mock<ICollectArchiveStore>().Object);
        RenderPopoverHost();

        var cut = Context.Render<RecordDetail>(parameters => parameters.Add(page => page.MonthKey, "202610").Add(page => page.RecordId, 22));

        Assert.Contains("压力", cut.Markup);
        Assert.Contains("正常", cut.Markup);
        Assert.Contains("配方号", cut.Markup);
        Assert.Contains("未配置判定", cut.Markup);
        Assert.Contains("力值", cut.Markup);
        Assert.Contains("缺数据", cut.Markup);
    }

    [Fact]
    public async Task Quick_chips_apply_process_abnormal_and_ng_filters()
    {
        var cut = Render();
        Assert.Contains("快捷筛选", cut.Markup);
        Assert.Contains("仅流程异常", cut.Markup);
        Assert.Contains("仅 NG", cut.Markup);

        await cut.InvokeAsync(() => cut.FindAll("div[aria-label='快捷筛选'] .mud-chip")
            .Single(chip => chip.TextContent.Contains("仅流程异常"))
            .Click());
        _store.Verify(store => store.QueryAsync(It.Is<CollectQueryRequest>(request =>
            request.ResultCode == ResultCodes.ProcessAbnormal), It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        await cut.InvokeAsync(() => cut.FindAll("div[aria-label='快捷筛选'] .mud-chip")
            .Single(chip => chip.TextContent.Contains("仅 NG"))
            .Click());
        _store.Verify(store => store.QueryAsync(It.Is<CollectQueryRequest>(request =>
            request.Judgement == Judgement.Ng), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public void List_row_exposes_serial_copy_and_trace_entry()
    {
        var cut = Render();
        Assert.NotNull(cut.Find("[aria-label='复制流水号']"));
        Assert.NotNull(cut.Find("[aria-label='复制托盘码']"));
        var trace = cut.FindAll("a").Single(a => a.TextContent.Contains("追溯") && (a.GetAttribute("href") ?? "").Contains("view=trace"));
        Assert.Contains("view=trace", trace.GetAttribute("href") ?? "");
        Assert.Contains("trace=", trace.GetAttribute("href") ?? "");
    }

    [Fact]
    public void Trace_panel_offers_print_and_export_for_a_loaded_session()
    {
        var cut = RenderTrace(Trace("20260919-000001", Judgement.Ng), "20260919-000001");
        Assert.Contains("打印履历", cut.Markup);
        Assert.Contains("导出履历", cut.Markup);
    }

    private IRenderedComponent<ProductTraceabilityPanel> RenderTrace(CollectSessionTrace trace, string serial)
    {
        _store.Setup(store => store.FindSessionTracesBySerialNoAsync(serial, It.IsAny<CancellationToken>()))
            .ReturnsAsync([trace]);
        Context.Services.AddSingleton(_store.Object);
        RenderPopoverHost();
        return Context.Render<ProductTraceabilityPanel>(parameters => parameters.Add(panel => panel.InitialSerialNo, serial));
    }

    /// <summary>一条判定为 <paramref name="judgement"/> 的会话履历；NG 时带上首因四件套。</summary>
    private static CollectSessionTrace Trace(string serial, Judgement judgement)
    {
        var start = new DateTime(2026, 9, 19, 10, 0, 0);
        var ng = judgement == Judgement.Ng;
        return new CollectSessionTrace
        {
            MonthKey = "202609",
            SessionId = 7,
            Session = new PalletSession
            {
                Id = 7,
                SerialNo = serial,
                PalletCode = "P0001",
                StartTime = start,
                EndTime = start.AddMinutes(5),
                Status = SessionStatus.Closed,
                Judgement = judgement,
                FirstNgStationCode = ng ? "ST020" : null,
                FirstNgAt = ng ? start.AddSeconds(3) : null,
                FirstNgResultCode = ng ? ResultCodes.QualityRejected : null,
                FirstNgReason = ng ? "压力超限" : null
            },
            Records =
            [
                new CollectRecord
                {
                    Id = 11,
                    PalletSessionId = 7,
                    SerialNo = serial,
                    PalletCode = "P0001",
                    StationId = 10,
                    StationCode = "ST010",
                    TriggerTime = start,
                    CompleteTime = start.AddSeconds(1),
                    ResultCode = ResultCodes.Success,
                    Judgement = Judgement.Ok
                }
            ]
        };
    }

    [Fact]
    public void Export_writes_an_audit_entry_naming_the_range_and_the_filters()
    {
        var cut = Render();

        ClickButton(cut, "导出 CSV");

        // 键=区间，变更列=过滤条件与条数：日志页的"键"列才不至于塞进一长串条件。
        _audit.Verify(
            a => a.WriteAsync("admin", "Export", "Query", It.IsAny<string>(), null, It.IsAny<string>(),
                default, "Success", "Blazor Server UI", null, null),
            Times.Once);
        Assert.Equal("已导出 1 条", Toast.LastMessage);
    }

    [Fact]
    public void A_failed_audit_write_does_not_look_like_a_failed_export()
    {
        _audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>(),
                "Success", "Blazor Server UI", null, null))
            .ThrowsAsync(new InvalidOperationException("config.db 被占用"));
        var cut = Render();

        ClickButton(cut, "导出 CSV");

        Assert.Contains("已导出 1 条", Toast.LastMessage);
        Assert.Contains("审计记录写入失败", Toast.LastMessage);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
    }

    /// <summary>
    /// 点表头是服务端排序：重新发一次查询（不是只排当前页），并且回到第 1 页 ——
    /// 换了次序，原来的第 N 页是另一批记录。
    /// </summary>
    [Fact]
    public async Task Clicking_a_column_header_sorts_on_the_server_and_returns_to_page_one()
    {
        // 造出不止一页：翻页控件得有第 2 页可点。
        _store
            .Setup(s => s.QuerySessionsAsync(It.IsAny<SessionQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionQueryResult { Total = 0, Items = Array.Empty<SessionListItem>() });
        _store
            .Setup(s => s.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult { Total = 60, Items = [] });

        var cut = Render();
        await cut.InvokeAsync(() => cut.FindComponent<MudPagination>().Instance.SelectedChanged.InvokeAsync(2));

        // 先确认真的停在第 2 页，否则下面的"回到第 1 页"是句空话。
        _store.Verify(
            s => s.QueryAsync(It.Is<CollectQueryRequest>(r => r.Skip == 20), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // 时间列默认最近在前，其余列默认升序（按工站/托盘码看时是从前往后读）。
        ClickButton(cut, "托盘码");
        _store.Verify(
            s => s.QueryAsync(
                It.Is<CollectQueryRequest>(r => r.SortBy == CollectSortField.PalletCode && !r.SortDescending && r.Skip == 0),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // 同一列再点一次：换方向，仍从第 1 页开始。
        ClickButton(cut, "托盘码");
        _store.Verify(
            s => s.QueryAsync(
                It.Is<CollectQueryRequest>(r => r.SortBy == CollectSortField.PalletCode && r.SortDescending && r.Skip == 0),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        // 地址栏带上排序列：刷新、转发、从明细页返回都停在同一批结果上。
        var uri = Context.Services.GetRequiredService<NavigationManager>().Uri;
        Assert.Contains("sort=pallet", uri);
    }

    [Fact]
    public async Task Sequential_time_page_uses_the_previous_page_cursor()
    {
        _store.SetupSequence(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult { Total = 60, Items = [], NextCursor = "cursor-page-2" })
            .ReturnsAsync(new CollectQueryResult { Total = 60, Items = [] });

        var cut = Render();
        await cut.InvokeAsync(() => cut.FindComponent<MudPagination>().Instance.SelectedChanged.InvokeAsync(2));

        _store.Verify(store => store.QueryAsync(
            It.Is<CollectQueryRequest>(request => request.AfterCursor == "cursor-page-2" && request.Skip == 20 && request.Take == 20),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Selecting_all_recipes_sends_no_recipe_filter()
    {
        var cut = Render();

        await SelectRecipeAsync(cut, null);

        _store.Verify(
            s => s.QueryAsync(It.Is<CollectQueryRequest>(r => r.RecipeCode == null), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task Selecting_no_recipe_asks_for_records_without_a_recipe()
    {
        var cut = Render();

        await SelectRecipeAsync(cut, "");

        _store.Verify(
            s => s.QueryAsync(It.Is<CollectQueryRequest>(r => r.RecipeCode == ""), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task A_recipe_code_that_looks_like_a_sentinel_stays_selectable()
    {
        // 哨兵是"取值里的筛选状态"，不是编码文本：真有人把型号编码取成 __EMPTY__，
        // 也得能和"未选型号"分开筛。
        _store
            .Setup(s => s.ListRecipeCodesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["__EMPTY__"]);
        var cut = Render();

        var chosen = await SelectRecipeAsync(cut, "__EMPTY__");

        _store.Verify(
            s => s.QueryAsync(It.Is<CollectQueryRequest>(r => r.RecipeCode == "__EMPTY__"), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        // 回读仍是这个编码，且与选项本身相等：被读成哨兵的话 MudSelect 就认不出选中项了。
        var bound = cut.FindComponent<MudSelect<RecipeFilterValue>>().Instance.Value;
        Assert.Equal("__EMPTY__", bound?.Recipe);
        Assert.Equal(chosen, bound);
    }

    [Fact]
    public async Task A_recipe_code_that_looks_like_a_sentinel_is_not_confused_with_the_sentinel()
    {
        _store
            .Setup(s => s.ListRecipeCodesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["__EMPTY__"]);
        var cut = Render();

        var sentinel = await SelectRecipeAsync(cut, "");
        var code = await SelectRecipeAsync(cut, "__EMPTY__");

        // 两个选项必须是不相等的取值，否则选中态会串台。
        Assert.NotEqual(sentinel, code);
        Assert.Equal("", sentinel.Recipe);
        Assert.Equal("__EMPTY__", code.Recipe);
    }

    [Fact]
    public async Task A_recipe_code_that_starts_with_the_option_prefix_round_trips()
    {
        // 取值直接带编码，不再有"前缀叠层"的往返：编码叫 c:X 就按 c:X 精确匹配。
        _store
            .Setup(s => s.ListRecipeCodesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["c:X"]);
        var cut = Render();

        var chosen = await SelectRecipeAsync(cut, "c:X");

        _store.Verify(
            s => s.QueryAsync(It.Is<CollectQueryRequest>(r => r.RecipeCode == "c:X"), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        Assert.Equal(chosen, cut.FindComponent<MudSelect<RecipeFilterValue>>().Instance.Value);
    }

    /// <summary>按角色发身份：页面只读 Name 与角色声明。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Export_checks_the_actual_result_for_truncation_and_cancel_does_not_download(bool confirmed)
    {
        var cut = Render();
        _store.Setup(store => store.QueryAsync(It.IsAny<CollectQueryRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CollectQueryResult
            {
                Total = SystemDefaults.ExportRowLimit + 1,
                Items = [new CollectRecordListItem { MonthKey = "202610", Record = new CollectRecord { PalletCode = "P1" } }]
            });
        Dialogs.MessageBoxResult = confirmed;
        ClickButton(cut, "导出 CSV");
        var dialog = Assert.Single(Dialogs.MessageBoxes);
        Assert.Equal("确认截断导出", dialog.Title);
        Assert.Contains("其余记录不会包含", dialog.Message);
        Assert.Equal(confirmed, Context.JSInterop.Invocations.Any(invocation => invocation.Identifier == "dtDownload"));
        _audit.Verify(audit => audit.WriteAsync("admin", "Export", "Query", It.IsAny<string>(), null,
            It.IsAny<string>(), It.IsAny<CancellationToken>(),
            "Success", "Blazor Server UI", null, null), confirmed ? Times.Once() : Times.Never());
        Assert.False(cut.FindAll("button").Single(button => button.TextContent.Contains("导出 CSV")).HasAttribute("disabled"));
    }

    [Fact]
    public async Task Export_csv_keeps_data_columns_and_carries_complete_query_context()
    {
        Config.Snapshot = new AppConfigurationSnapshot { Stations = [new Station { Id = 23, Code = "ST/023", Name = "工站" }] };
        var cut = Render();
        await cut.InvokeAsync(() => cut.FindComponent<QueryPresetsPanel>().Instance.Apply.InvokeAsync(
            "query?station=23&pallet=P&serial=S&judge=Ng&result=11&recipe=NEW&alias=OLD&alias=OLDER&sort=duration&dir=asc&from=2026-10-09T20:00:00&to=2026-10-10T07:59:59.9999999"));
        ClickButton(cut, "导出 CSV");
        var download = Assert.Single(Context.JSInterop.Invocations["dtDownload"]);
        Assert.Contains("ST023_20261009200000-20261010075959", (string)download.Arguments[0]!);
        var csv = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)download.Arguments[1]!)).TrimStart('\uFEFF');
        using var reader = new CsvHelper.CsvReader(new StringReader(csv), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(reader.Read());
        reader.ReadHeader();
        Assert.Equal(new[] { "触发时间", "流水号", "托盘码", "工站", "型号", "判定", "结果码", "结果说明", "耗时(ms)", "查询上下文" }, reader.HeaderRecord);
        Assert.True(reader.Read());
        Assert.Equal("P0001", reader.GetField("托盘码"));
        var summary = reader.GetField("查询上下文");
        Assert.Contains("2026-10-09 20:00:00.0000000", summary);
        Assert.Contains("2026-10-10 07:59:59.9999999", summary);
        Assert.Contains("pallet=P;serial=S;station=23;judge=Ng;result=11;recipe=NEW;aliases=OLD,OLDER;", summary);
        Assert.Contains("sort=duration;dir=asc", summary);
        Assert.Contains("导出=1;截断=否", summary);
        Assert.False(reader.Read());
    }

    private sealed class StubAuthenticationStateProvider(string role) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity(
                [
                    new Claim(ClaimTypes.Name, "admin"),
                    new Claim(ClaimTypes.Role, role)
                ],
                authenticationType: "test");

            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
