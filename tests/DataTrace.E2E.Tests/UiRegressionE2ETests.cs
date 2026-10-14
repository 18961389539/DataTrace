using Microsoft.Playwright;
using DataTrace.Application.Alarms;
using DataTrace.Domain.Entities;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 界面回归用例：钉住那些"只有真渲染才看得见"的约定。
/// 组件层测试能证明参数传对了，但证明不了一件 CSS 或一次 DOM 结构有没有悄悄退化，
/// 而这几处恰恰都是曾经出过问题的地方。
/// </summary>
[Collection("e2e")]
public class UiRegressionE2ETests : E2ETestBase
{
    public UiRegressionE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    /// <summary>按可访问名称点击按钮；需要时启用全名匹配以避开同名按钮。</summary>
    private async Task ClickAsync(string text, bool exact = false)
    {
        await WaitForCircuitReadyAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = text, Exact = exact }).ClickAsync();
    }

    private async Task OpenDialogAsync(ILocator trigger)
    {
        await WaitForCircuitReadyAsync();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await trigger.ClickAsync();
            try
            {
                await Page.Locator(".mud-dialog").WaitForAsync(new() { Timeout = 1500 });
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(250);
            }
        }

        throw new TimeoutException("重复点击后仍未打开对话框。");
    }

    /// <summary>等结果行出现。首条采集记录要等模拟器跑完一轮，机器忙时会晚几秒。</summary>
    private async Task WaitForResultRowsAsync(int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            await ClickAsync("查询", exact: true);
            try
            {
                await WaitBodyContainsAsync("明细", 3000);
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(1000);
            }
        }

        throw new TimeoutException($"{timeoutMs}ms 内没有采到可查询的记录。");
    }

    [Fact]
    public async Task SkipLinkShowsOnFocusAndMovesFocusToMainContent()
    {
        await OpenAsync("/");

        var skipLink = Page.GetByRole(AriaRole.Link, new() { Name = "跳至主要内容" });
        await skipLink.FocusAsync();
        Assert.True(await skipLink.IsVisibleAsync());

        await skipLink.PressAsync("Enter");
        var navigation = await Page.EvaluateAsync<string>("""
            () => JSON.stringify({
                hash: location.hash,
                activeTag: document.activeElement?.tagName,
                activeId: document.activeElement?.id
            })
            """);
        Assert.Contains("\"hash\":\"#main-content\"", navigation);
        Assert.Contains("\"activeId\":\"main-content\"", navigation);
    }

    [Fact]
    public async Task RecipeLimitDialogKeepsTouchTargetsAtLeast44Pixels()
    {
        await using var touch = await Browser.NewContextAsync(new()
        {
            HasTouch = true,
            IsMobile = true,
            ViewportSize = new() { Width = 430, Height = 932 }
        });
        var page = await touch.NewPageAsync();
        await page.AddStyleTagAsync(new()
        {
            Content = "*, *::before, *::after { animation-duration: 0s !important; transition-duration: 0s !important; }"
        });
        await page.GotoAsync(App.BaseUrl + "/config/recipes");
        await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('.dt-page-title')");

        await page.Locator("table tbody button", new() { HasText = "限值" }).First.ClickAsync();
        await page.Locator(".mud-dialog").WaitForAsync();
        await page.WaitForTimeoutAsync(400);
        var namedInput = page.Locator(".mud-dialog input[aria-label$='规格下限']").First;
        var accessibleName = await namedInput.GetAttributeAsync("aria-label");
        Assert.NotNull(accessibleName);
        Assert.EndsWith("规格下限", accessibleName);
        Assert.Equal(1, await page.GetByLabel(accessibleName, new() { Exact = true }).CountAsync());

        var undersized = await page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('.mud-dialog .mud-button-root, .mud-dialog .mud-icon-button')]
                .filter(element => {
                    const rect = element.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0
                        && (rect.width < 43.9 || rect.height < 43.9);
                })
                .map(element => {
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    const parent = element.closest('.mud-dialog');
                    const transform = parent ? getComputedStyle(parent).transform : 'none';
                    return `${element.getAttribute('aria-label') || element.textContent.trim()} ${rect.width}x${rect.height} min=${style.minWidth}/${style.minHeight} transform=${transform}`;
                })
            """);

        Assert.True(undersized.Length == 0, string.Join("; ", undersized));
    }

    [Fact]
    public async Task TouchscreenTargetsStayUsableAtPhoneAndTabletWidths()
    {
        foreach (var width in new[] { 430, 820 })
        {
            await using var touch = await Browser.NewContextAsync(new() { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = width, Height = 932 } });
            var page = await touch.NewPageAsync();
            foreach (var route in new[] { "/query", "/config/stations", "/alarms" })
            {
                await page.GotoAsync(App.BaseUrl + route);
                await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('.dt-page-title')");
                var small = await page.EvaluateAsync<string[]>("""
                    () => [...document.querySelectorAll('.dt-main .mud-button-root, .dt-main .station-list-item-select')]
                        .filter(element => { const rect = element.getBoundingClientRect();
                            return rect.width > 0 && rect.height > 0 && (rect.width < 43.9 || rect.height < 43.9); })
                        .map(element => element.getAttribute('aria-label') || element.textContent.trim())
                    """);
                Assert.Empty(small);
                var outsideInputs = await page.EvaluateAsync<int>("""
                    () => [...document.querySelectorAll('.mud-input-numeric-spin button')].filter(button => {
                        const input = button.closest('.mud-input').getBoundingClientRect();
                        const rect = button.getBoundingClientRect();
                        return rect.height > 0 && (rect.top < input.top - 1 || rect.bottom > input.bottom + 1);
                    }).length
                    """);
                Assert.Equal(0, outsideInputs);
                Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"));
                await TouchScreenshotAsync(page, $"touch-{width}-{route.Trim('/').Replace('/', '-')}.png");
            }
        }
    }

    [Theory]
    [InlineData(430)]
    [InlineData(820)]
    public async Task TouchscreenScanningAndDetailNavigationKeepContext(int width)
    {
        await using var touch = await Browser.NewContextAsync(new() { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = width, Height = 932 } });
        var page = await touch.NewPageAsync();
        await page.GotoAsync(App.BaseUrl + "/query?dir=asc&size=100");
        await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('.dt-page-title')");
        var rows = page.Locator(".dt-query-results tbody tr").Filter(new() { HasText = "明细" });
        for (var attempt = 0; await rows.CountAsync() < 3 && attempt < 30; attempt++)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "查询", Exact = true }).TapAsync();
            await Task.Delay(500);
        }
        Assert.True(await rows.CountAsync() >= 3);
        var pallet = (await rows.First.Locator("td").Nth(2).InnerTextAsync()).Trim();
        var scanner = page.GetByLabel("托盘码", new() { Exact = true });
        await scanner.PressSequentiallyAsync(pallet, new() { Delay = 450 });
        await scanner.PressAsync("Enter");
        Assert.Equal(pallet, await scanner.InputValueAsync());
        await page.WaitForFunctionAsync("pallet => location.search.includes('pallet=' + encodeURIComponent(pallet))", pallet);
        await page.GetByRole(AriaRole.Button, new() { Name = "查询", Exact = true }).WaitForAsync();
        await page.Locator(".dt-query-results .mud-table-container").EvaluateAsync("element => element.scrollTop = Math.min(160, element.scrollHeight - element.clientHeight)");
        await page.GetByRole(AriaRole.Link, new() { Name = "明细", Exact = true }).Nth(1).TapAsync();
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/query/\\d{6}/\\d+"));
        await page.GetByRole(AriaRole.Link, new() { Name = "下一条", Exact = true }).WaitForAsync();
        var original = page.Url;
        var scroll = await page.EvaluateAsync<double[]>("() => { const state = JSON.parse(sessionStorage.getItem('dt-query-scroll')); return [state.top, state.table]; }");
        await TouchScreenshotAsync(page, $"touch-{width}-record.png");
        await page.GetByRole(AriaRole.Link, new() { Name = "下一条", Exact = true }).TapAsync();
        await page.WaitForFunctionAsync("original => location.href !== original", original);
        await page.GetByRole(AriaRole.Link, new() { Name = "上一条", Exact = true }).TapAsync();
        await page.WaitForURLAsync(original);
        await page.GetByRole(AriaRole.Link, new() { Name = "返回查询", Exact = true }).TapAsync();
        await page.WaitForFunctionAsync("scroll => { const table = document.querySelector('.dt-query-results .mud-table-container'); return table && Math.abs(table.scrollTop - scroll[1]) < 2 && Math.abs(window.scrollY - scroll[0]) < 2; }", scroll);
        Assert.Contains("dir=asc", page.Url);
        Assert.Contains("pallet=" + Uri.EscapeDataString(pallet), page.Url);
        await TouchScreenshotAsync(page, $"touch-{width}-query-restored.png");
    }

    [Theory]
    [InlineData(430)]
    [InlineData(820)]
    public async Task TouchscreenAlarmCanBeTakenAndRecordsTheOwner(int width)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(App.DataRoot, "config.db"), DefaultTimeout = 30 };
        await using var db = new ConfigDbContext(new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection.ToString()).Options);
        var marker = "TOUCH-ALARM-" + Guid.NewGuid().ToString("N");
        var incident = new AlarmIncident { Key = marker, Message = marker, Kind = (int)LineAlarmKind.ConsecutiveNg, RaisedAt = DateTime.Now, ClearedAt = DateTime.Now };
        db.AlarmIncidents.Add(incident);
        await db.SaveChangesAsync();
        try
        {
            await using var touch = await Browser.NewContextAsync(new() { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = width, Height = 932 } });
            var page = await touch.NewPageAsync();
            await page.GotoAsync(App.BaseUrl + "/alarms");
            await page.WaitForFunctionAsync("() => document.activeElement === document.querySelector('.dt-page-title')");
            var row = page.Locator("tr").Filter(new() { HasText = marker });
            var take = row.GetByRole(AriaRole.Button, new() { Name = "接手", Exact = true });
            var rect = await take.BoundingBoxAsync();
            Assert.NotNull(rect);
            Assert.True(rect.Width >= 44 && rect.Height >= 44);
            await take.TapAsync();
            await page.WaitForFunctionAsync("marker => [...document.querySelectorAll('tr')].some(row => row.textContent.includes(marker) && row.textContent.includes('admin'))", marker);
            await db.Entry(incident).ReloadAsync();
            Assert.Equal("admin", incident.AcknowledgedBy);
            Assert.NotNull(incident.AcknowledgedAt);
            await TouchScreenshotAsync(page, $"touch-{width}-alarm-taken.png");
        }
        finally
        {
            db.AlarmIncidents.Remove(incident);
            await db.SaveChangesAsync();
        }
    }

    private static async Task TouchScreenshotAsync(IPage page, string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "artifacts");
        Directory.CreateDirectory(directory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name), FullPage = true });
    }

    [Theory]
    [InlineData(430)]
    [InlineData(820)]
    [InlineData(1440)]
    public async Task UsabilityQuerySchemesPersistAndExportContext(int width)
    {
        await Page.SetViewportSizeAsync(width, 932);
        await OpenAsync("/query");
        await WaitForResultRowsAsync();
        if (width <= 600)
        {
            await Page.Locator(".dt-query-advanced-toggle").ClickAsync();
        }
        await Page.GetByRole(AriaRole.Button, new() { Name = "时间区间 近 7 天", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "保存查询方案", Exact = true }).ClickAsync();
        var name = "重点工站每班次产品质量异常与采集记录查询方案-" + Guid.NewGuid().ToString("N")[..8];
        await Page.GetByLabel("方案名称", new() { Exact = true }).FillAsync(name);
        await TouchScreenshotAsync(Page, $"usability-{width}-scheme-editor.png");
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"));
        await Page.GetByRole(AriaRole.Button, new() { Name = "保存方案", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).WaitForAsync();
        await Page.EvaluateAsync("""
            () => {
                const key = Object.keys(localStorage).find(key => key.startsWith('dt-query-presets:'));
                const presets = JSON.parse(localStorage.getItem(key));
                const url = new URL(presets[presets.length - 1].url, location.origin);
                url.searchParams.set('from', '2025-01-01');
                url.searchParams.set('to', '2025-01-01');
                presets[presets.length - 1].url = url.pathname.replace(/^\//, '') + url.search;
                localStorage.setItem(key, JSON.stringify(presets));
            }
            """);
        await OpenAsync("/query");
        await Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();
        await Page.WaitForFunctionAsync("date => location.search.includes(date)", DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd"));
        if (width <= 600)
        {
            await Page.Locator(".dt-query-advanced-toggle").ClickAsync();
        }
        await Page.Locator(".dt-query-results tbody tr").Filter(new() { HasText = "明细" }).First.WaitForAsync();
        var download = await Page.RunAndWaitForDownloadAsync(() => Page.GetByRole(AriaRole.Button, new() { Name = "导出 CSV", Exact = true }).ClickAsync());
        Assert.Contains("all-stations_", download.SuggestedFilename);
        var csv = await File.ReadAllTextAsync((await download.PathAsync())!);
        Assert.Contains("耗时(ms),查询上下文", csv);
        Assert.Contains("区间（含起止）=", csv);
        Assert.Contains("sort=time;dir=desc", csv);
        Assert.Contains("截断=否", csv);
        Assert.True(await Page.EvaluateAsync<bool>("""
            () => { dtQueryPresets.set('other-account', [{name: 'Other only', url: 'query', range: 'today'}]);
                const isolated = dtQueryPresets.get('other-account');
                return isolated.length === 1 && isolated[0].name === 'Other only'; }
            """));
        await TouchScreenshotAsync(Page, $"usability-{width}-scheme-applied.png");
        await Page.GetByRole(AriaRole.Button, new() { Name = "删除方案 " + name, Exact = true }).ClickAsync();
        await Page.Locator(".mud-dialog").GetByRole(AriaRole.Checkbox).CheckAsync();
        await Page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "删除方案", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    [Theory]
    [InlineData(430)]
    [InlineData(1440)]
    public async Task UsabilityInvalidStationSaveFocusesTheActualInput(int width)
    {
        await Page.SetViewportSizeAsync(width, 932);
        await OpenAsync("/config/stations");
        await WaitForCircuitReadyAsync();
        await Page.GetByLabel("触发值", new() { Exact = true }).FillAsync("0");
        await Page.Locator(".mud-tab").Filter(new() { HasText = "曲线" }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^保存 ") }).ClickAsync();
        await Page.WaitForFunctionAsync("() => document.querySelector('[data-dt-validation=triggerValue]')?.contains(document.activeElement)");
        Assert.Contains("工站未保存：", await Page.Locator("body").InnerTextAsync());
        var input = await Page.GetByLabel("触发值", new() { Exact = true }).BoundingBoxAsync();
        Assert.NotNull(input);
        Assert.True(input.Y >= 0 && input.Y + input.Height <= 932);
        Assert.True(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1"));
        await TouchScreenshotAsync(Page, $"usability-{width}-validation-focus.png");
        await Page.GetByLabel("触发值", new() { Exact = true }).FillAsync("1");
    }

    [Fact]
    public async Task DateInputsNeverClipTheValue()
    {
        // 曾经每个筛选项按 12 栏切成 168px，而日期控件至少需要 180px，
        // "2026/9/21" 就被裁成 "2026/9/2" —— 用户看不到自己正在按哪天过滤。
        await OpenAsync("/query");
        await WaitForAsync(".dt-range input");

        var clipped = await Page.EvaluateAsync<bool>("""
            () => [...document.querySelectorAll('.dt-range input')]
                    .some(i => i.scrollWidth > i.clientWidth + 1)
            """);
        var values = await Page.EvaluateAsync<string[]>("""
            () => [...document.querySelectorAll('.dt-range input')].map(i => i.value)
            """);

        Assert.False(clipped, "日期输入框把内容裁掉了");
        Assert.Equal(2, values.Length);
        Assert.All(values, v => Assert.Matches(@"^\d{4}/\d{1,2}/\d{1,2}$", v));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/query")]
    [InlineData("/reports")]
    [InlineData("/curve-baseline")]
    [InlineData("/logs")]
    [InlineData("/config/plc")]
    [InlineData("/config/stations")]
    [InlineData("/config/recipes")]
    [InlineData("/config/settings")]
    [InlineData("/simulate")]
    [InlineData("/diagnostics")]
    [InlineData("/users")]
    public async Task SharedPageHeadingsStayCompactWithoutAFocusRing(string path)
    {
        // 两半都要成立：焦点确实落到页标题上（键盘与读屏用户才知道页面换了），
        // 而那是程序性聚焦，不该在标题周围画出橙色焦点环。
        // 逐路由都测是因为这条只靠约定、没有编译期检查 —— 页面标题必须保留统一焦点类。
        await OpenAsync(path);

        // 标题在静态预渲染的 HTML 里就有了，而移动焦点要等 SignalR circuit 起来之后，
        // 所以等"焦点真的落上去了"而不是等元素存在 —— 后者会读到还没接管的页面。
        await Page.WaitForFunctionAsync("""
            () => document.activeElement === document.querySelector('.dt-page-title')
            """, arg: null, new() { Timeout = 20000 });

        var outline = await Page.EvaluateAsync<string>("""
            () => getComputedStyle(document.activeElement).outlineStyle
            """);
        var fontSize = await Page.Locator("h1.dt-page-title").EvaluateAsync<double>("""
            element => Number.parseFloat(getComputedStyle(element).fontSize)
            """);

        Assert.Equal("none", outline);
        Assert.InRange(fontSize, 18, 20);
    }

    [Fact]
    public async Task QueryFiltersRoundTripThroughTheAddressBar()
    {
        await OpenAsync("/query");
        await WaitForAsync(".dt-range button");
        // 预设按钮在预渲染 HTML 里就有，但 circuit 没接管前点它不会写回地址栏。
        await WaitForCircuitReadyAsync();

        await ClickAsync("近 7 天");
        await Page.WaitForFunctionAsync("() => location.search.includes('from=')");
        var filtered = Page.Url;

        await Page.GotoAsync(filtered);
        await WaitForAsync(".dt-range input");
        await WaitForCircuitReadyAsync();
        var from = await Page.InputValueAsync(".dt-range input");

        Assert.DoesNotContain("?", from);
        Assert.NotEqual(DateTime.Today.ToString("yyyy/M/d"), from);

        // 重置必须连地址一起清掉，否则刷新会把条件"幽灵回填"。
        await ClickAsync("重置");
        await Page.WaitForFunctionAsync("() => location.search === ''");

        Assert.DoesNotContain("from=", Page.Url);
    }

    [Fact]
    public async Task LongResultTablesKeepTheirHeaderPinned()
    {
        await OpenAsync("/query?size=100");
        await WaitForResultRowsAsync();

        // 断言契约而不是模拟滚动：滚动是否真的生效取决于当次有多少行，
        // 而这个特性失效的两种真实方式都能由下面两条抓到 ——
        // 参数名写错（MudBlazor 7.16 是 FixedHeader，写成 StickyHeader 会被静默吞掉，th 仍是 static）
        // 和没给容器高度（容器没有内滚范围，sticky 无从吸附）。
        var thPosition = await Page.EvaluateAsync<string>("""
            () => getComputedStyle(document.querySelector('.mud-table-container thead th')).position
            """);
        var container = await Page.EvaluateAsync<int[]>("""
            () => { const c = document.querySelector('.mud-table-container');
                    return [Math.round(c.getBoundingClientRect().height), Math.round(c.clientHeight)]; }
            """);

        Assert.Equal("sticky", thPosition);
        Assert.True(container[0] > 0 && container[0] < 900, $"表格容器高度不受约束：{container[0]}px");
    }

    [Fact]
    public async Task DetailPageShowsTheLimitsTheJudgementActuallyUsed()
    {
        await OpenAsync("/query");
        await WaitForResultRowsAsync();

        // OP130、OP140、OP150 都没有用户提供的判定限值；选择仍配置了压力规格的 ST040，
        // 确认详情展示的是该条记录实际使用的判定快照。
        var stationRow = Page.Locator("table tbody tr")
            .Filter(new() { HasText = "ST040" })
            .Filter(new() { HasText = "明细" })
            .First;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await stationRow.CountAsync() == 0 && DateTime.UtcNow < deadline)
        {
            await ClickAsync("查询", exact: true);
            await Task.Delay(1000);
        }

        Assert.True(await stationRow.CountAsync() > 0, "查询结果中没有 ST040 记录");
        for (var attempt = 0; new Uri(Page.Url).PathAndQuery == "/query"; attempt++)
        {
            Assert.True(attempt < 10, "点了 10 次仍未打开记录明细");
            await stationRow.ClickAsync();
            await Page.WaitForTimeoutAsync(400);
        }

        // 详情是异步取数的：骨架屏上就有"返回查询"链接，等它出现就读表头会读到还没渲染记录的壳
        // （表头集合为空，或读到查询页残留的表头）。等表头里真的出现"规格限"再断言。
        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('table thead th')].some(th => th.textContent.includes('规格限'))
            """);

        var headers = await Page.Locator("table thead th").AllInnerTextsAsync();
        Assert.Contains("规格限", headers);
        Assert.Contains("预警限", headers);

        // 最新一条是仿真器刚采的，四道限值应当都落库了，不能显示成"未配置"。
        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('table tbody td')]
                    .some(td => /\d/.test(td.textContent) && !td.textContent.includes('未配置')
                                && td.textContent.includes('~'))
            """);
        var body = await Page.InnerTextAsync("body");
        Assert.DoesNotContain("未配置", body);
    }

    /// <summary>
    /// 过程能力那块曾经只由单测背书：报表页改一版布局（分段渲染）就可能整个不出现，
    /// 而单测照样全绿。这里补一条"界面真的画出来了"的兜底。
    /// </summary>
    [Fact]
    public async Task CapabilitySectionRendersCardsAndChartForTheSelectedTag()
    {
        await OpenAsync("/reports");
        await WaitBodyContainsAsync("过程能力");

        // MudTabs 只渲染当前页签的面板，过程能力必须点过去才在 DOM 里。
        // 等的文案取面板自己的「Cpk（组内）」而不是图标题：图标题要有采样点才出现，
        // 而这个实例的数据目录是每轮新建的，开局还没采到样本，等图标题会先超时。
        await ClickTabUntilAsync("过程能力", "Cpk（组内）");
        await RefreshUntilCapabilityHasSamplesAsync();

        await Page.Locator(".dt-chart canvas").First.WaitForAsync();
        // 演示实例没改过规格限，只应该有一段：分段提示与分段表都不该出现。
        var body = await Page.InnerTextAsync("body");
        Assert.DoesNotContain("区间内规格限变更", body);
        Assert.Equal(0, await Page.Locator("th", new() { HasText = "时间范围" }).CountAsync());
    }

    /// <summary>
    /// ECharts 的轴悬停提示显示当前采样点，并保留应用约定的读数顺序。
    /// </summary>
    [Fact]
    public async Task HoveringAChartShowsThePointReadout()
    {
        await OpenAsync("/reports");
        await WaitBodyContainsAsync("过程能力");

        await ClickTabUntilAsync("过程能力", "Cpk（组内）");
        await RefreshUntilCapabilityHasSamplesAsync();

        // MudTabs may retain an inactive panel. A hidden chart still has an ECharts instance and
        // tooltip node, so selecting the first DOM match can move the real mouse over a zero-sized
        // canvas while waiting on that hidden tooltip forever.
        var chart = Page.Locator(".dt-chart:visible").First;
        await chart.Locator("canvas").WaitForAsync();
        await chart.ScrollIntoViewIfNeededAsync();
        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('.dt-chart')].some(element => {
                const rect = element.getBoundingClientRect();
                const instance = window.echarts?.getInstanceByDom(element);
                if (!instance || rect.width <= 0 || rect.height <= 0) return false;
                return (instance.getOption()?.series ?? []).some(series =>
                    (series.data ?? []).some(point => point?.tip && Array.isArray(point.value)));
            })
            """);
        var point = await chart.EvaluateAsync<double[]>("""
            element => {
                const instance = window.echarts?.getInstanceByDom(element);
                const option = instance?.getOption();
                if (!instance || instance.getWidth() <= 0 || instance.getHeight() <= 0) return [];
                const seriesIndex = (option.series ?? []).findIndex(series =>
                    (series.data ?? []).some(point => point?.tip && Array.isArray(point.value)));
                if (seriesIndex < 0) return [];
                const data = option.series[seriesIndex].data.filter(point => point?.tip && Array.isArray(point.value));
                if (data.length === 0) return [];

                // Stay away from the x-axis endpoints, where an axis pointer can land on the
                // border instead of inside the grid. Use a real plotted x value and the grid's
                // vertical midpoint so the mouse is unambiguously inside the plot area.
                const sample = data[Math.floor(data.length / 2)];
                const x = instance.convertToPixel({ xAxisIndex: 0 }, sample.value[0]);
                const yAxis = Array.isArray(option.yAxis) ? option.yAxis[0] : option.yAxis;
                const y = instance.convertToPixel(
                    { yAxisIndex: 0 },
                    (Number(yAxis.min) + Number(yAxis.max)) / 2);
                if (!instance.containPixel({ gridIndex: 0 }, [x, y])) return [];

                const rect = element.getBoundingClientRect();
                return [rect.left + x, rect.top + y];
            }
            """);
        Assert.Equal(2, point.Length);
        await Page.Mouse.MoveAsync((float)point[0], (float)point[1]);

        var readout = Page.Locator(".echarts-tooltip:visible").First;
        await readout.WaitForAsync(new() { Timeout = 5000 });

        var text = await readout.InnerTextAsync();
        Assert.Contains("→", text);
        Assert.Contains("采样点", text);
    }

    /// <summary>
    /// 切页签。预渲染的 DOM 会在 circuit 接手时被重建，把早点的那一下抹掉
    /// （与登录表是同一个窗口），所以点了要复核结果，没生效就再点。
    /// </summary>
    private Task ClickTabUntilAsync(string tabText, string expectedText)
        => Page.WaitForFunctionAsync(
            """
            ([tabText, expectedText]) => {
                const tab = [...document.querySelectorAll('.mud-tab')]
                    .find(t => t.textContent.includes(tabText));
                if (!tab) {
                    return false;
                }

                tab.click();
                return document.body.innerText.includes(expectedText);
            }
            """,
            new[] { tabText, expectedText });

    /// <summary>
    /// 等过程能力拿到样本。页面只在加载和点「刷新报表」时取数，而数据要等模拟器跑，
    /// 所以这里反复触发统计，而不是点一次干等。
    /// </summary>
    private async Task RefreshUntilCapabilityHasSamplesAsync(int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var ready = await Page.EvaluateAsync<bool>("""
                () => {
                    const text = document.body.innerText;
                    return text.includes('Cpk（组内）')
                        && !text.includes('区间内没有采样数据')
                        && !text.includes('请先在「参数趋势」页签选择一个数值点位');
                }
                """);
            if (ready)
            {
                return;
            }

            await ClickAsync("刷新报表");
            await Task.Delay(1000);
        }

        throw new TimeoutException($"{timeoutMs}ms 内该点位没有采到样本，过程能力画不出图。");
    }

    [Fact]
    public async Task SettingsWarnsAboutUnsavedEditsAndClearsItAfterSaving()
    {
        await OpenAsync("/config/settings");
        await WaitForAsync("text=系统设置");
        // 打字前必须等 circuit 接手：输入框在预渲染的 HTML 里就已经存在，而接管时这段 DOM
        // 会被整段重建，那个窗口里发出的 input 会被丢掉——失败方式就是"敲了 90，页面上什么都没发生"。
        await WaitForCircuitReadyAsync();

        Assert.Equal(0, await Page.Locator("text=未保存").CountAsync());

        var scanInterval = Page.GetByLabel("扫描间隔(ms)");
        var original = await scanInterval.InputValueAsync();

        try
        {
            await scanInterval.ClickAsync();
            await scanInterval.FillAsync("90");
            await scanInterval.PressAsync("Tab");

            await WaitForAsync("text=未保存");

            // 页面已处于交互状态。ClickAsync 会重复等初始标题焦点信号，输入框操作后焦点已不在标题上。
            await Page.GetByRole(AriaRole.Button, new() { Name = "保存" }).ClickAsync();
            await WaitBodyContainsAsync("已保存并下发采集端");

            // 胶囊是标题的兄弟节点而不是子孙，所以按文字等它消失。
            await Page.WaitForFunctionAsync("""
                () => ![...document.querySelectorAll('.mud-chip')].some(c => c.textContent.includes('未保存'))
                """, arg: null, new() { Timeout = 10000 });
        }
        finally
        {
            // 整个 collection 共用一个应用实例，而本用例是真的把配置写进了库。
            // 不回滚的话，后面每个用例都跑在被这里改过的扫描周期上。
            await scanInterval.ClickAsync();
            await scanInterval.FillAsync(original);
            await scanInterval.PressAsync("Tab");
            await Page.GetByRole(AriaRole.Button, new() { Name = "保存" }).ClickAsync();
        }
    }

    [Fact]
    public async Task RecipeDialogRejectsATargetOutsideTheSpecBand()
    {
        // 这条规则原本在工站配置页和型号限值对话框各写一份，而对话框那份漏了目标值两条，
        // 同一组数字一边拒绝一边放行。现在两边共用域层那一份。
        await OpenAsync("/config/recipes");
        await WaitForAsync("text=产品型号");

        await OpenDialogAsync(Page.Locator("table tbody button", new() { HasText = "限值" }).First);

        var pressureRow = Page.Locator(".mud-dialog tbody tr")
            .Filter(new() { HasText = "ST040" })
            .Filter(new() { HasText = "压力" })
            .First;
        var target = pressureRow.Locator(".dt-limit-field input").Nth(4);
        await target.ClickAsync();
        await target.FillAsync("9999");
        await target.PressAsync("Tab");

        await Page.Locator(".mud-dialog button", new() { HasText = "保存" }).ClickAsync();

        await WaitForAsync(".mud-dialog .dt-row-error");
        Assert.Contains("目标值不能高于规格上限", await PageInnerTextInDialogAsync());
    }

    [Fact]
    public async Task CancellingThePlcDialogLeavesTheTableAlone()
    {
        // 对话框曾经直接绑定父页表格里的同一个实体，点「取消」并不会把改到一半的
        // 名称和 IP 撤回去 —— 表格会继续显示一个从来没被保存过的连接。
        await OpenAsync("/config/plc");
        await WaitForAsync("text=编辑");

        var firstRow = Page.Locator("table tbody tr").First;
        var before = await firstRow.InnerTextAsync();
        Assert.NotEmpty(before.Trim());

        await OpenDialogAsync(firstRow.GetByRole(AriaRole.Button, new() { Name = "编辑" }));

        var nameLabel = Page.Locator(".mud-dialog label", new() { HasText = "名称" }).First;
        var nameId = await nameLabel.GetAttributeAsync("for");
        var nameInput = Page.Locator($".mud-dialog #{nameId}");
        await nameInput.ClickAsync();
        await nameInput.FillAsync("不该被留下的名字");
        await nameInput.PressAsync("Tab");

        // 先确认改动真的写进去了，否则下面"取消后表格没变"会因为什么都没发生而空过。
        Assert.Equal("不该被留下的名字", await nameInput.InputValueAsync());

        await Page.Locator(".mud-dialog button", new() { HasText = "取消" }).ClickAsync();
        await Page.WaitForSelectorAsync(".mud-dialog", new() { State = WaitForSelectorState.Detached });

        Assert.DoesNotContain("不该被留下的名字", await Page.InnerTextAsync("body"));
        Assert.Equal(before.Trim(), (await firstRow.InnerTextAsync()).Trim());
    }

    [Fact]
    public async Task IconOnlyButtonsExposeARealAccessibleName()
    {
        // 写成 AriaLabel="…" 能编译、能渲染、页面也看不出问题，但 MudBlazor 会把它落成
        // arialabel 属性（分析器 MUD0002 报的就是这个），而 ARIA 只认 aria-label ——
        // 读屏软件什么都读不到。所以按"可访问名"查，而不是按属性名查。
        await OpenAsync("/query");
        await WaitForResultRowsAsync();

        Assert.True(
            await Page.GetByRole(AriaRole.Button, new() { Name = "复制托盘码" }).CountAsync() > 0,
            "复制按钮没有可供读屏使用的可访问名");

        Assert.True(
            await Page.GetByRole(AriaRole.Button, new() { Name = "展开或收起菜单" }).CountAsync() > 0,
            "菜单按钮没有可供读屏使用的可访问名");

        Assert.Equal(0, await Page.EvaluateAsync<int>("() => document.querySelectorAll('[arialabel]').length"));
    }

    [Fact]
    public async Task DashboardKpiSummaryKeepsSupportingTextReadable()
    {
        await Page.AddInitScriptAsync("localStorage.removeItem('dt-shopfloor');");
        await OpenAsync("");
        await WaitForCircuitReadyAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "大屏模式" }).ClickAsync();
        await Page.WaitForFunctionAsync("() => document.body.classList.contains('dt-shopfloor')");

        var firstKpi = Page.Locator(".dt-kpi-card").First;
        var desktopLayout = await firstKpi.EvaluateAsync<bool[]>("""
            element => {
                const main = element.querySelector('.dt-kpi-main').getBoundingClientRect();
                const hints = [...element.querySelectorAll('.dt-kpi-hint')];
                return [
                    getComputedStyle(element).display === 'flex',
                    hints.length > 0 && main.bottom <= hints[0].getBoundingClientRect().top + 1,
                    hints.every(hint => {
                        const style = getComputedStyle(hint);
                        return style.whiteSpace === 'normal'
                            && style.textOverflow === 'clip'
                            && hint.innerText.trim().length > 0
                            && hint.scrollWidth <= hint.clientWidth + 1;
                    }),
                    element.scrollWidth <= element.clientWidth
                ];
            }
            """);

        Assert.True(desktopLayout[0], "KPI 内容应使用稳定的弹性布局");
        Assert.True(desktopLayout[1], "解释信息应位于主指标之后，不与主值重叠");
        Assert.True(desktopLayout[2], "辅助说明应完整换行显示，不能截断或溢出");
        Assert.True(desktopLayout[3], "KPI 内容不应横向溢出");

        await Page.SetViewportSizeAsync(390, 844);
        var mobileWidths = await Page.Locator(".dt-kpi-card").EvaluateAllAsync<double[]>(
            "elements => elements.map(element => element.getBoundingClientRect().width)");

        Assert.True(mobileWidths[0] > mobileWidths[1] * 1.6, "窄屏首张 KPI 应跨两列以容纳完整单行内容");
        Assert.True(
            await firstKpi.EvaluateAsync<bool>("""
                element => element.scrollWidth <= element.clientWidth
                    && [...element.querySelectorAll('.dt-kpi-hint')].every(hint =>
                        getComputedStyle(hint).whiteSpace === 'normal'
                        && hint.scrollWidth <= hint.clientWidth + 1)
                """),
            "窄屏 KPI 说明应完整换行且不横向溢出");
    }

    [Fact]
    public async Task ShopFloorModeHidesAppBarAndKeepsExitAction()
    {
        await Page.AddInitScriptAsync("localStorage.removeItem('dt-shopfloor');");
        await OpenAsync("");
        await WaitForCircuitReadyAsync();

        var menu = Page.Locator(".dt-appbar-menu");
        var utilities = Page.Locator(".dt-appbar-utilities");
        var identity = Page.Locator(".dt-appbar-identity");
        Assert.NotEqual("none", await menu.EvaluateAsync<string>("element => getComputedStyle(element).display"));
        Assert.NotEqual("none", await utilities.EvaluateAsync<string>("element => getComputedStyle(element).display"));
        Assert.NotEqual("none", await identity.EvaluateAsync<string>("element => getComputedStyle(element).display"));

        await Page.GetByRole(AriaRole.Button, new() { Name = "大屏模式" }).ClickAsync();
        await Page.WaitForFunctionAsync("() => document.body.classList.contains('dt-shopfloor')");

        Assert.False(await Page.Locator(".mud-appbar").IsVisibleAsync());
        Assert.Equal(1, await Page.GetByRole(AriaRole.Button, new() { Name = "退出大屏" }).CountAsync());

        await Page.GetByRole(AriaRole.Button, new() { Name = "退出大屏" }).ClickAsync();
        await Page.WaitForFunctionAsync("() => !document.body.classList.contains('dt-shopfloor')");

        Assert.NotEqual("none", await menu.EvaluateAsync<string>("element => getComputedStyle(element).display"));
        Assert.NotEqual("none", await utilities.EvaluateAsync<string>("element => getComputedStyle(element).display"));
        Assert.NotEqual("none", await identity.EvaluateAsync<string>("element => getComputedStyle(element).display"));
    }

    [Fact]
    public async Task ThemeSwitchPersistsAcrossReload()
    {
        await OpenAsync("");
        await WaitForCircuitReadyAsync();

        var appBar = Page.Locator(".mud-appbar");
        var lightAppBarColor = await appBar.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        var darkSwitch = Page.GetByRole(AriaRole.Button, new() { Name = "切换到深色主题" });
        await ActUntilAsync(() => darkSwitch.ClickAsync(), "切换到浅色主题", "切换到深色主题");

        await Page.WaitForFunctionAsync(
            "(lightColor) => document.documentElement.classList.contains('dt-theme-dark') " +
            "&& getComputedStyle(document.querySelector('.mud-appbar')).backgroundColor !== lightColor",
            lightAppBarColor,
            new PageWaitForFunctionOptions { Timeout = 10000 });

        var darkAppBarColor = await appBar.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        Assert.Equal("dark", await Page.EvaluateAsync<string>("() => localStorage.getItem('dt-theme-mode')"));
        Assert.Equal(
            "切换到浅色主题",
            await Page.GetByRole(AriaRole.Button, new() { Name = "切换到浅色主题" }).GetAttributeAsync("aria-label"));

        await ReloadAsync();
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.classList.contains('dt-theme-dark') " +
            "&& localStorage.getItem('dt-theme-mode') === 'dark'");
        await Page.WaitForFunctionAsync(
            "color => getComputedStyle(document.querySelector('.mud-appbar')).backgroundColor === color",
            darkAppBarColor);

        Assert.Equal(darkAppBarColor, await appBar.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
        Assert.Equal(
            "切换到浅色主题",
            await Page.GetByRole(AriaRole.Button, new() { Name = "切换到浅色主题" }).GetAttributeAsync("aria-label"));
    }

    [Fact]
    public async Task RecipeRareActionsAreGroupedInAnAccessibleMoreMenu()
    {
        await OpenAsync("/config/recipes");
        var moreActions = Page.Locator("button[aria-label^='更多型号操作：']");
        await moreActions.First.WaitForAsync();
        await moreActions.First.ClickAsync();

        await Page.GetByText("复制型号", new() { Exact = true }).WaitForAsync();
        await Page.GetByText("删除型号", new() { Exact = true }).WaitForAsync();
        Assert.True(await moreActions.CountAsync() > 0);
    }

    [Fact]
    public async Task PwaManifestWorkerAndInstallActionAreAvailable()
    {
        await OpenAsync("");
        await WaitForCircuitReadyAsync();

        Assert.Equal(
            "manifest.webmanifest",
            await Page.Locator("link[rel='manifest']").GetAttributeAsync("href"));

        var manifestDetails = await Page.EvaluateAsync<string>("""
            async () => {
                const response = await fetch('/manifest.webmanifest');
                const manifest = await response.json();
                const png192 = manifest.icons.find(icon => icon.sizes === '192x192' && icon.purpose === 'any');
                const png512 = manifest.icons.find(icon => icon.sizes === '512x512' && icon.purpose === 'any');
                const maskable = manifest.icons.find(icon => icon.sizes === '512x512' && icon.purpose === 'maskable');
                const iconResponse = await fetch(png192.src);
                return [
                    manifest.display,
                    manifest.start_url,
                    png192?.type,
                    png512?.type,
                    maskable?.type,
                    iconResponse.ok,
                    response.headers.get('content-type').split(';')[0]
                ].join('|');
            }
            """);
        Assert.Equal(
            "standalone|/|image/png|image/png|image/png|true|application/manifest+json",
            manifestDetails);

        await Page.WaitForFunctionAsync("""
            async () => {
                const registration = await navigator.serviceWorker.getRegistration();
                return Boolean(registration?.active?.scriptURL.endsWith('/service-worker.js'));
            }
            """);

        await Page.EvaluateAsync("""
            () => {
                const event = new Event('beforeinstallprompt', { cancelable: true });
                event.prompt = async () => {};
                event.userChoice = Promise.resolve({ outcome: 'accepted' });
                window.dispatchEvent(event);
            }
            """);

        var installButton = Page.GetByRole(AriaRole.Button, new() { Name = "安装 DataTrace 应用" });
        await installButton.WaitForAsync();
        await installButton.ClickAsync();
        await installButton.WaitForAsync(new() { State = WaitForSelectorState.Detached });
    }

    private Task<string> PageInnerTextInDialogAsync() => Page.Locator(".mud-dialog").InnerTextAsync();
}
