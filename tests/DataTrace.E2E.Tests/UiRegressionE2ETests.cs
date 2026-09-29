using Microsoft.Playwright;

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

    /// <param name="exact">
    /// 按名字全等匹配。默认子串匹配：查询页现在同时有模式切换按钮「记录查询」和表单提交按钮「查询」，
    /// 按 "查询" 找会一次命中两个，Playwright 直接抛 strict mode violation —— 四条用例同一个根因。
    /// </param>
    private Task ClickAsync(string name, bool exact = false)
        => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = exact }).ClickAsync();

    /// <summary>
    /// 点开一个对话框，必要时重试。
    /// 按钮文字在静态预渲染的 HTML 里就有了，但点击要等 SignalR circuit 接管之后才会真的
    /// 走到 Blazor 事件处理 —— 只点一次会时好时坏，而且失败方式往往是"什么都没发生"，
    /// 于是后续断言空过，测试报绿却什么都没证明。
    /// </summary>
    private async Task OpenDialogAsync(ILocator button, int attempts = 10)
    {
        for (var i = 0; i < attempts; i++)
        {
            await button.ClickAsync();
            try
            {
                await button.Page.WaitForSelectorAsync(
                    ".mud-dialog", new() { Timeout = 2000, State = WaitForSelectorState.Visible });
                return;
            }
            catch (TimeoutException)
            {
                await Task.Delay(400);
            }
        }

        throw new TimeoutException($"点了 {attempts} 次仍未打开对话框。");
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
    [InlineData("/users")]
    public async Task SharedPageHeadingsStayCompactWithoutAFocusRing(string path)
    {
        // 两半都要成立：焦点确实落到页标题上（键盘与读屏用户才知道页面换了），
        // 而那是程序性聚焦，不该在标题周围画出橙色焦点环。
        // 逐路由都测是因为这条只靠约定、没有编译期检查 —— 新页面若用裸 Typo.h5
        // 而不是 PageHeader，焦点环就会悄悄回来。
        await OpenAsync(path);

        // 标题在静态预渲染的 HTML 里就有了，而移动焦点要等 SignalR circuit 起来之后，
        // 所以等"焦点真的落上去了"而不是等元素存在 —— 后者会读到还没接管的页面。
        await Page.WaitForFunctionAsync("""
            () => document.activeElement === document.querySelector('h5')
            """, arg: null, new() { Timeout = 20000 });

        var outline = await Page.EvaluateAsync<string>("""
            () => getComputedStyle(document.activeElement).outlineStyle
            """);
        var fontSize = await Page.Locator("h5.dt-page-title").EvaluateAsync<double>("""
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

        // 点的这一下同样可能落在 circuit 接手的窗口里被丢掉（什么都没发生），
        // 所以点完复核地址栏：没跳转就再点一次。
        for (var attempt = 0; ; attempt++)
        {
            if (new Uri(Page.Url).PathAndQuery != "/query")
            {
                break;
            }

            Assert.True(attempt < 10, "点了 10 次仍未打开记录明细");
            await Page.Locator("table tbody tr").First.ClickAsync();
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
        await Page.GotoAsync($"{App.BaseUrl}/reports");
        await WaitBodyContainsAsync("过程能力");

        // MudTabs 只渲染当前页签的面板，过程能力必须点过去才在 DOM 里。
        // 等的文案取面板自己的「Cpk（组内）」而不是图标题：图标题要有采样点才出现，
        // 而这个实例的数据目录是每轮新建的，开局还没采到样本，等图标题会先超时。
        await ClickTabUntilAsync("过程能力", "Cpk（组内）");
        await RefreshUntilCapabilityHasSamplesAsync();

        // LineChart 的 svg 自己带 dt-chart 类，不是 .dt-chart 的子元素。
        await Page.Locator("svg.dt-chart").First.WaitForAsync();
        // 演示实例没改过规格限，只应该有一段：分段提示与分段表都不该出现。
        var body = await Page.InnerTextAsync("body");
        Assert.DoesNotContain("区间内规格限变更", body);
        Assert.Equal(0, await Page.Locator("th", new() { HasText = "时间范围" }).CountAsync());
    }

    /// <summary>
    /// 悬停读数：鼠标进入曲线后，读数面板要真的显形并带上那一点的数值。
    /// 读数由 js/datatrace.js 绑（点位 → 找最近的点 → 挪面板），这条用例守住整条链路：
    /// 服务端得渲染出点位与 data-tip，脚本得绑上，面板得从 hidden 变可见。
    /// 不校验具体数值（那是 LineChartTests 的事）。
    /// </summary>
    [Fact]
    public async Task HoveringAChartShowsThePointReadout()
    {
        await OpenAsync("/reports");
        await WaitBodyContainsAsync("过程能力");

        await ClickTabUntilAsync("过程能力", "Cpk（组内）");
        await RefreshUntilCapabilityHasSamplesAsync();

        var chart = Page.Locator("svg.dt-chart").First;
        await chart.WaitForAsync();
        await chart.ScrollIntoViewIfNeededAsync();

        // 悬停读数由 JS 在图表渲完后绑定（见 dtChartReadout.bind：读数值取自点位自带的 data-tip）。
        // 得等点位真的带上 data-tip 再悬停 —— 早悬停的话那一刻既没有可读点数、绑定也可能还没跑，
        // "等面板可见"就会一直等不到（表现为 20s 超时）。
        await Page.WaitForFunctionAsync(
            "() => document.querySelector('.dt-chart-box circle.dt-point[data-tip]') !== null");

        var box = await chart.BoundingBoxAsync();
        Assert.NotNull(box);
        await Page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + box.Height / 2);

        // 面板常驻 DOM 但带 hidden；等它可见就是等 JS 把读数填出来了。
        var readout = Page.Locator(".dt-chart-readout").First;
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

            await ClickAsync("保存");
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
            await ClickAsync("保存");
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

        var target = Page.Locator(".mud-dialog .dt-limit-field input").Nth(4);
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
    public async Task DashboardKpiSummaryStaysOnOneLine()
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
                const hint = element.querySelector('.dt-kpi-hint').getBoundingClientRect();
                return [
                    getComputedStyle(element).display === 'flex',
                    main.top < hint.bottom && hint.top < main.bottom,
                    element.scrollWidth <= element.clientWidth
                ];
            }
            """);

        Assert.True(desktopLayout[0], "KPI 内容应使用单行布局");
        Assert.True(desktopLayout[1], "辅助统计应与主指标处于同一行");
        Assert.True(desktopLayout[2], "KPI 内容不应横向溢出");

        await Page.SetViewportSizeAsync(390, 844);
        var mobileWidths = await Page.Locator(".dt-kpi-card").EvaluateAllAsync<double[]>(
            "elements => elements.map(element => element.getBoundingClientRect().width)");

        Assert.True(mobileWidths[0] > mobileWidths[1] * 1.6, "窄屏首张 KPI 应跨两列以容纳完整单行内容");
        Assert.True(
            await firstKpi.EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth"),
            "窄屏 KPI 内容不应溢出");
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

        await Page.ReloadAsync();
        await Page.WaitForFunctionAsync(
            "() => document.documentElement.classList.contains('dt-theme-dark') " +
            "&& localStorage.getItem('dt-theme-mode') === 'dark'");
        await WaitForCircuitReadyAsync();

        Assert.Equal(darkAppBarColor, await appBar.EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor"));
        Assert.Equal(
            "切换到浅色主题",
            await Page.GetByRole(AriaRole.Button, new() { Name = "切换到浅色主题" }).GetAttributeAsync("aria-label"));
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
