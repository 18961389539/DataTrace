using System.Security.Claims;
using DataTrace.Application.Configuration;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DataTrace.Web.Tests;

/// <summary>
/// 报表页：型号筛选必须作用于整份报表、统计失败不能留旧数字、导出要有角色闸门与留痕。
/// 这三条都是"看着有数其实不对"的类型，单测统计服务抓不到，只能在页面上钉。
/// </summary>
public class ReportsPageTests : WebTestBase
{
    private readonly Mock<IRuntimeStore> _store = new();
    private readonly Mock<IReportService> _reports = new();
    private readonly Mock<ISpcService> _spc = new();
    private readonly Mock<IAuditLogger> _audit = new();
    private string _role = AppRoles.Administrator;

    private static readonly DateTime SampleTime = new(2026, 9, 19, 8, 0, 0);

    public ReportsPageTests()
    {
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Stations =
            [
                new Station
                {
                    Id = 10,
                    Code = "ST010",
                    Name = "压装",
                    Tags =
                    [
                        new TagDefinition
                        {
                            Id = 1,
                            StationId = 10,
                            Code = "ST010_P1",
                            Name = "压力",
                            DataType = PlcDataType.Float
                        }
                    ]
                }
            ],
            Recipes = [new Recipe { Id = 7, Code = "A100", Name = "型号 A100" }]
        };

        _store
            .Setup(s => s.ListRecipeCodesAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        _reports
            .Setup(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ThroughputReport
            {
                ByDay = [],
                ByRecipe = [new RecipeThroughput { RecipeCode = "A100", Total = 3, Ok = 3 }]
            });
        _reports
            .Setup(r => r.GetDefectTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueTopItem>());
        _reports
            .Setup(r => r.GetWarningTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IssueTopItem>());
        _reports
            .Setup(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrendPoint { Time = SampleTime, Value = 12.5, PalletCode = "P0001" }]);
        _spc
            .Setup(s => s.GetProcessCapabilityAsync(It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProcessCapabilityReport?)null);
    }

    private IRenderedComponent<Reports> Render()
    {
        Context.Services.AddSingleton(_store.Object);
        Context.Services.AddSingleton(_reports.Object);
        Context.Services.AddSingleton(_spc.Object);
        Context.Services.AddSingleton(_audit.Object);
        Context.Services.AddSingleton<AuthenticationStateProvider>(new StubAuthenticationStateProvider(_role));
        // MudSelect 的浮层要一个 provider：真实应用由 MainLayout 提供。
        Context.RenderComponent<MudPopoverProvider>();

        return Context.RenderComponent<Reports>();
    }

    /// <summary>
    /// 按下拉的取值切换型号筛选。这几组值就是界面的取值契约：
    /// "*"=全部、__EMPTY__=未选型号、带 "c:" 前缀的是真实编码。
    /// </summary>
    private static async Task SelectRecipeAsync(IRenderedComponent<Reports> cut, string optionValue)
    {
        var select = cut.FindComponents<MudSelect<string>>()[0];
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(optionValue));
    }

    [Fact]
    public async Task Recipe_filter_reaches_every_query_on_the_page()
    {
        var cut = Render();

        await SelectRecipeAsync(cut, "c:A100");

        // 一条都不能漏：漏了就会出现"选了 A100 却看到全部型号的不良"。
        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), "A100", It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetDefectTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), "A100", It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetWarningTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), "A100", It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, "A100", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _spc.Verify(s => s.GetProcessCapabilityAsync(1, It.IsAny<DateTime>(), It.IsAny<DateTime>(), "A100", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);

        // 每次加载只查一次产量：按日与按型号是同一份数据的两个切面，不该把区间内记录查两遍。
        // 上面那条 A100 断言覆盖改筛选后那一次加载，这条覆盖首次加载（不限型号）。
        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Selecting_no_recipe_asks_for_records_without_a_recipe()
    {
        var cut = Render();

        await SelectRecipeAsync(cut, "__EMPTY__");

        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), "", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_recipe_code_that_looks_like_a_sentinel_stays_selectable()
    {
        // 旧实现把 __EMPTY__ 当"未选型号"的哨兵，真有人把型号编码取成这个名字就再也筛不出来了。
        var cut = Render();

        await SelectRecipeAsync(cut, "c:__EMPTY__");

        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), "__EMPTY__", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("c:__EMPTY__", cut.FindComponents<MudSelect<string>>()[0].Instance.Value);
    }

    [Fact]
    public async Task A_failed_reload_clears_every_table_including_the_recipe_breakdown()
    {
        var cut = Render();
        Assert.Contains("A100", cut.Markup);

        _reports
            .Setup(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("月库被占用"));
        ClickButton(cut, "刷新报表");

        // 进度条一消失还留着旧数字，就是"看着有数、其实是旧的"——这张表最容易漏。
        Assert.DoesNotContain("A100", cut.Markup);
        Assert.Contains("统计失败", Toast.LastMessage);
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

        Assert.Equal(visible, cut.FindAll("button").Any(b => b.TextContent.Contains("导出趋势")));
    }

    /// <summary>
    /// 采样上限是"少算了就要说出来"：截断时必须出现提示，没截断时不能凭空警告。
    /// </summary>
    [Fact]
    public void Trend_truncation_is_stated_on_the_page_only_when_it_happens()
    {
        var cut = Render();
        Assert.DoesNotContain("单次上限", cut.Markup);

        // 页面多取一点来判断有没有截断，所以超过上限就是"上限 + 1"。
        var overLimit = Enumerable.Range(0, TrendLimitProbe)
            .Select(i => new TrendPoint { Time = SampleTime.AddSeconds(i), Value = 12.5, PalletCode = "P0001" })
            .ToList();
        _reports
            .Setup(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(overLimit);

        ClickButton(cut, "刷新报表");

        Assert.Contains("单次上限", cut.Markup);

        // 上限之外的那一点不进内存、也不进导出。
        ClickButton(cut, "导出趋势");
        Assert.Contains("已导出 20000 个趋势点", Toast.LastMessage);
    }

    /// <summary>页面请求的是"上限 + 1"，用来区分"刚好到上限"和"被截断"。</summary>
    private const int TrendLimitProbe = 20001;

    [Fact]
    public async Task Conditions_are_written_back_to_the_address_bar()
    {
        var cut = Render();

        await SelectRecipeAsync(cut, "c:A100");

        // 默认区间与默认点位不写进地址，其余条件要能刷新/转发后复原。
        Assert.Contains("recipe=A100", cut.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Export_writes_an_audit_entry_naming_the_range_and_the_filters()
    {
        var cut = Render();

        ClickButton(cut, "导出趋势");
        await cut.InvokeAsync(() => Task.CompletedTask);

        _audit.Verify(
            a => a.WriteAsync("admin", "Export", "Report", It.IsAny<string>(), null, It.IsAny<string>()),
            Times.Once);
        Assert.Equal("已导出 1 个趋势点", Toast.LastMessage);
    }

    [Fact]
    public async Task A_failed_audit_write_does_not_look_like_a_failed_export()
    {
        _audit
            .Setup(a => a.WriteAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("config.db 被占用"));
        var cut = Render();

        ClickButton(cut, "导出趋势");
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.Contains("已导出 1 个趋势点", Toast.LastMessage);
        Assert.Contains("审计记录写入失败", Toast.LastMessage);
        Assert.Equal(Severity.Warning, Toast.LastSeverity);
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
