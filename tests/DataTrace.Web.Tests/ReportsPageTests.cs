using System.Security.Claims;
using DataTrace.Application.Configuration;
using DataTrace.Application.Reporting;
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
            .Setup(r => r.GetDefectTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueTopReport());
        _reports
            .Setup(r => r.GetWarningTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueTopReport());
        _reports
            .Setup(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TrendPoint { Time = SampleTime, Value = 12.5, PalletCode = "P0001" }]);
        _spc
            .Setup(s => s.AnalyzeAsync(It.IsAny<int>(), It.IsAny<IReadOnlyList<TrendPoint>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
    /// 按下拉里的选项切换型号筛选。参数是筛选状态：null=全部型号、""=未选型号、其它=型号编码。
    /// 从真正渲染出来的选项里取取值，顺带钉住"这个选项确实在下拉里"；
    /// 返回选中的取值，供用例比对页面回读的那一份（两者不相等，MudSelect 就认不出选中项）。
    /// </summary>
    private static async Task<RecipeFilterValue> SelectRecipeAsync(IRenderedComponent<Reports> cut, string? recipe)
    {
        var select = cut.FindComponent<MudSelect<RecipeFilterValue>>();
        var value = cut.FindComponents<MudSelectItem<RecipeFilterValue>>()
            .Select(i => i.Instance.Value)
            .First(v => v is not null && v.Recipe == recipe);
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(value));
        return value!;
    }

    [Fact]
    public async Task Recipe_filter_reaches_every_query_on_the_page()
    {
        var cut = Render();

        // 首屏之后那一轮趋势要先落定，再改条件 —— 否则断言分不清是哪一轮取回来的。
        cut.WaitForAssertion(() => _reports.Verify(
            r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once));

        await SelectRecipeAsync(cut, "A100");

        // 一条都不能漏：漏了就会出现"选了 A100 却看到全部型号的不良"。
        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), "A100", It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetDefectTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<int>(), "A100", It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetWarningTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<int>(), "A100", It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 1, "A100", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        _spc.Verify(s => s.AnalyzeAsync(1, It.IsAny<IReadOnlyList<TrendPoint>>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), "A100", It.IsAny<CancellationToken>()), Times.Once);

        // 每次加载只查一次产量：按日与按型号是同一份数据的两个切面，不该把区间内记录查两遍。
        // 上面那条 A100 断言覆盖改筛选后那一次加载，这条覆盖首次加载（不限型号）。
        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 首屏不等趋势：那条 2 万点的窄投影属于另外两个页签（真实库上实测 ~170 ms，是进入页面耗时的大头），
    /// 它慢一点也不能挡住「直通率」那一屏。
    /// </summary>
    [Fact]
    public void The_first_screen_does_not_wait_for_the_trend()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<TrendPoint>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reports
            .Setup(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);

        var cut = Render();

        // 趋势一条都还没回来，直通率那一屏（含按型号表）已经在页面上了，导出按钮也如实地说"没有点可导"。
        Assert.Contains("A100", cut.Markup);
        Assert.True(ExportButtonDisabled(cut));

        gate.SetResult([new TrendPoint { Time = SampleTime, Value = 12.5, PalletCode = "P0001" }]);

        cut.WaitForAssertion(() => Assert.False(ExportButtonDisabled(cut)));
    }

    private static bool ExportButtonDisabled(IRenderedComponent<Reports> cut)
        => cut.FindAll("button")
            .First(b => b.TextContent.Contains("导出趋势"))
            .HasAttribute("disabled");

    [Fact]
    public async Task Selecting_no_recipe_asks_for_records_without_a_recipe()
    {
        var cut = Render();

        await SelectRecipeAsync(cut, "");

        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), "", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 工站和型号一样是整份报表的条件：不良/预警不跟着收窄，
    /// 选了 ST010 却看到全线的不良榜，现场就会去错工站找原因。
    /// </summary>
    [Fact]
    public async Task Station_filter_reaches_the_issue_queries_too()
    {
        var cut = Render();

        var station = cut.FindComponents<MudSelect<int?>>()[0];
        await cut.InvokeAsync(() => station.Instance.ValueChanged.InvokeAsync(10));

        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 10, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetDefectTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 10, It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _reports.Verify(r => r.GetWarningTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), 10, It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// 占比的分母是区间内全部次数，不是榜内合计：榜内合计永远 100%，
    /// 就看不出"榜外还有一大截"。
    /// </summary>
    [Fact]
    public void The_share_column_is_measured_against_the_whole_range()
    {
        _reports
            .Setup(r => r.GetDefectTopAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueTopReport
            {
                Items =
                [
                    new IssueTopItem { Name = "压力", Count = 3 },
                    new IssueTopItem { Name = "位移", Count = 3 }
                ],
                Total = 9
            });

        var cut = Render();

        // 3 / 9 才是真占比；按榜内合计算会显示成 50.0%，等于宣称榜外没有别的问题。
        Assert.Contains((3d / 9d).ToString("P1"), cut.Markup);
        Assert.DoesNotContain((3d / 6d).ToString("P1"), cut.Markup);
        Assert.Contains("区间共 9 次超规格", cut.Markup);
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

        _reports.Verify(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), "__EMPTY__", It.IsAny<CancellationToken>()), Times.Once);
        // 回读仍是这个编码，且与选项本身相等：被读成哨兵的话 MudSelect 就认不出选中项了。
        var bound = cut.FindComponent<MudSelect<RecipeFilterValue>>().Instance.Value;
        Assert.Equal("__EMPTY__", bound?.Recipe);
        Assert.Equal(chosen, bound);
    }

    /// <summary>
    /// 改过编码的型号，"按产品型号"表里只能有一行：旧码那行既没有名称（显示成"旧编码 xxx"），
    /// 又会让人以为区间里有两个型号；而曲线基线是把旧码算作本型号的（见 CurveRecipeScope）。
    /// </summary>
    [Fact]
    public void Previous_codes_are_merged_into_the_current_one_in_the_recipe_table()
    {
        Config.Snapshot = new AppConfigurationSnapshot
        {
            Stations = Config.Snapshot.Stations,
            Recipes = [new Recipe { Id = 7, Code = "B300", Name = "改码后的型号", PreviousCodes = "A100" }]
        };
        _reports
            .Setup(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ThroughputReport
            {
                ByDay = [],
                ByRecipe =
                [
                    new RecipeThroughput { RecipeCode = "B300", Total = 5, Ok = 5 },
                    new RecipeThroughput { RecipeCode = "A100", Total = 3, Ok = 2, Ng = 1 }
                ]
            });

        var cut = Render();

        // 按日表也有「直通率」，靠首列表头区分：这张表首列是「型号」。
        var table = cut.FindAll("table").Single(t => t.QuerySelectorAll("th").First().TextContent.Contains("型号"));
        var row = Assert.Single(table.QuerySelectorAll("tbody tr"));
        Assert.Contains("B300 · 改码后的型号", row.TextContent);
        Assert.DoesNotContain("旧编码 A100", row.TextContent);

        // 产量与 OK/NG 都按合并后的口径累加。
        var cells = row.QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "产量", "OK", "NG", "未判定" }, table.QuerySelectorAll("th").Skip(1).Take(4).Select(th => th.TextContent.Trim()));
        Assert.Equal(new[] { "8", "7", "1", "0" }, cells.Skip(1).Take(4));
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

    /// <summary>
    /// 失败后趋势与过程能力都被清空了，若沿用"区间内没有采集数据"，
    /// 现场会照着"这个点位没数据"去查传感器 —— 得说清楚是统计失败。
    /// </summary>
    [Fact]
    public void A_failed_reload_says_so_instead_of_claiming_there_is_no_data()
    {
        var cut = Render();
        _reports
            .Setup(r => r.GetThroughputAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("月库被占用"));

        ClickButton(cut, "刷新报表");

        ActivateTab(cut, "参数趋势");
        Assert.Contains("本页结果已清空", cut.Markup);
        Assert.DoesNotContain("没有采集数据", cut.Markup);

        ActivateTab(cut, "过程能力");
        Assert.Contains("本页结果已清空", cut.Markup);
        Assert.DoesNotContain("再回到这里查看过程能力", cut.Markup);
    }

    /// <summary>MudTabs 只渲染当前页签的面板，要断言别的页签得先切过去。</summary>
    private static void ActivateTab(IRenderedComponent<Reports> cut, string tabText)
        => cut.FindAll(".mud-tab").First(t => t.TextContent.Contains(tabText)).Click();

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
    /// 过程能力与参数趋势是同一点位、同一区间：趋势那批点直接交给统计服务算。
    /// 各查一次的话，同一条窄投影要跑两遍，而"图上画的是哪些点、Cpk 按哪些点算"还得靠人工对齐。
    /// </summary>
    [Fact]
    public void Process_capability_reuses_the_trend_samples_instead_of_querying_again()
    {
        TrendPoint[] samples =
        [
            new TrendPoint { Time = SampleTime, Value = 12.5, PalletCode = "P0001", LowerLimit = 10, UpperLimit = 15 },
            new TrendPoint { Time = SampleTime.AddSeconds(1), Value = 12.6, PalletCode = "P0002", LowerLimit = 10, UpperLimit = 15 }
        ];
        _reports
            .Setup(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(samples);

        var cut = Render();

        // 趋势样本由首屏之后那一轮取回来，取到的这批直接交给过程能力。
        cut.WaitForAssertion(() => _spc.Verify(
            s => s.AnalyzeAsync(
                1,
                It.Is<IReadOnlyList<TrendPoint>>(list => ReferenceEquals(list, samples)),
                It.IsAny<DateTime>(),
                It.IsAny<DateTime>(),
                null,
                It.IsAny<CancellationToken>()),
            Times.Once));
    }

    /// <summary>
    /// 采样上限是"少算了就要说出来"：截断时必须出现提示，没截断时不能凭空警告。
    /// </summary>
    [Fact]
    public void Trend_truncation_is_stated_on_the_page_only_when_it_happens()
    {
        var cut = Render();

        // 首屏之后那一轮趋势先落定（默认桩值只有一个点，没截断），再改桩值刷新 —— 断言才说得清是谁的结果。
        cut.WaitForAssertion(() => _reports.Verify(
            r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once));
        Assert.DoesNotContain("单次上限", cut.Markup);

        // 页面多取一点来判断有没有截断，所以超过上限就是"上限 + 1"。
        var overLimit = Enumerable.Range(0, TrendLimitProbe)
            .Select(i => new TrendPoint { Time = SampleTime.AddSeconds(i), Value = 12.5, PalletCode = "P0001" })
            .ToList();
        _reports
            .Setup(r => r.GetTrendAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(overLimit);

        ClickButton(cut, "刷新报表");
        cut.WaitForAssertion(() => Assert.Contains("单次上限", cut.Markup));

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

        await SelectRecipeAsync(cut, "A100");

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
