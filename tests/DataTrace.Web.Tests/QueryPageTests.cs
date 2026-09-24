using System.Security.Claims;
using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using DataTrace.Web.Components.Shared;
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

    private IRenderedComponent<Query> Render()
    {
        Context.Services.AddSingleton(_store.Object);
        Context.Services.AddSingleton(_audit.Object);
        Context.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthenticationStateProvider(_role));
        // MudSelect 的浮层要一个 provider：真实应用由 MainLayout 提供。
        Context.RenderComponent<MudPopoverProvider>();

        return Context.RenderComponent<Query>();
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
    public void Export_writes_an_audit_entry_naming_the_range_and_the_filters()
    {
        var cut = Render();

        ClickButton(cut, "导出 CSV");

        // 键=区间，变更列=过滤条件与条数：日志页的"键"列才不至于塞进一长串条件。
        _audit.Verify(
            a => a.WriteAsync("admin", "Export", "Query", It.IsAny<string>(), null, It.IsAny<string>()),
            Times.Once);
        Assert.Equal("已导出 1 条", Toast.LastMessage);
    }

    [Fact]
    public void A_failed_audit_write_does_not_look_like_a_failed_export()
    {
        _audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("config.db 被占用"));
        var cut = Render();

        ClickButton(cut, "导出 CSV");

        Assert.Contains("已导出 1 条", Toast.LastMessage);
        Assert.Contains("审计记录写入失败", Toast.LastMessage);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
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