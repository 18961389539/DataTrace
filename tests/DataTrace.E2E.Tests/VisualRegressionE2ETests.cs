using System.Runtime.Versioning;
using System.Text.Json;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 视觉回归：布局几何断言（在任何机器上都稳定）+ 像素基线比对（样式真的被改动时才需要）。
/// </summary>
/// <remarks>
/// 为什么两种都要：几何断言能抓住"叠印""表格塌成一坨""横向溢出"这类真出过的事故，
/// 而且换浏览器版本不会抖；但它看不见颜色、圆角、字号这类纯观感的退化。
/// 像素基线正好反过来 —— 什么都能看见，但对渲染器差异敏感，所以它默认只在本地跑
/// （CI 设 DATATRACE_E2E_GOLDEN=off，理由见 <see cref="VisualGolden"/>）。
/// 截图只挑没有实时数据的外壳区域：看板上每几秒就刷新一次时间，那种图每次都不同。
/// </remarks>
[Collection("e2e-visual")]
public class VisualRegressionE2ETests : E2ETestBase
{
    private const int DesktopWidth = 1280;
    private const int DesktopHeight = 800;

    /// <summary>工控平板/手持终端的宽度：断点行为与桌面不同，叠印多发生在这里。</summary>
    private const int TabletWidth = 430;

    private static readonly string[] Routes =
    [
        "/", "/query", "/reports", "/curve-baseline", "/logs",
        "/config/plc", "/config/stations", "/config/recipes",
        "/config/settings", "/simulate", "/users"
    ];

    public VisualRegressionE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    [Theory]
    [MemberData(nameof(RoutesForDesktop))]
    public async Task NoRouteOverflowsHorizontallyOnDesktop(string path)
    {
        await Page.SetViewportSizeAsync(DesktopWidth, DesktopHeight);
        await Page.GotoAsync($"{App.BaseUrl}{path}");
        await WaitForAsync(".mud-layout");

        Assert.Equal(0, await HorizontalOverflowAsync());
    }

    [Fact]
    public async Task NarrowTabletWidthsStackInsteadOfOverflowing()
    {
        await Page.SetViewportSizeAsync(TabletWidth, 932);

        foreach (var path in new[] { "/", "/query", "/reports", "/config/settings", "/users" })
        {
            await Page.GotoAsync($"{App.BaseUrl}{path}");
            await WaitForAsync(".mud-layout");

            // 表格允许自己横向滚（.dt-scroll-x 就是干这个的），整页不允许。
            Assert.True(await HorizontalOverflowAsync() == 0, $"{path} 在 {TabletWidth}px 下把整页撑出了横向滚动");
        }
    }

    /// <summary>
    /// 筛选区曾经叠印过：预设按钮压在日期输入框上，控件互相盖住但 DOM 一切正常。
    /// 这里按同一层块级子元素的包围盒两两比对，并要求所有可交互控件都在卡片框内。
    /// </summary>
    [Fact]
    public async Task FilterCardKeepsItsBlocksApart()
    {
        await Page.SetViewportSizeAsync(DesktopWidth, DesktopHeight);
        await Page.GotoAsync($"{App.BaseUrl}/query");
        var card = await WaitForAsync(".dt-filter-card");

        var report = await card.EvaluateAsync<string>("""
            (card) => {
                const box = el => el.getBoundingClientRect();
                const COLLISION = 8;
                const collide = (a, b) => {
                    const w = Math.min(a.right, b.right) - Math.max(a.left, b.left);
                    const h = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top);
                    return w > COLLISION && h > COLLISION ? `${Math.round(w)}x${Math.round(h)}` : null;
                };
                const label = el => {
                    const b = box(el);
                    const text = (el.getAttribute('aria-label') || el.value || el.textContent || '').trim().slice(0, 12);
                    return `${el.tagName.toLowerCase()}“${text}”[${Math.round(b.x)},${Math.round(b.y)},${Math.round(b.width)}x${Math.round(b.height)}]`;
                };

                // 判据只看可交互控件：预设按钮压到日期输入框上就是这次要防的事故，
                // 而块级容器之间贴边重叠几像素是 MudGrid 栏距负外边距的设计，不算问题。
                const controls = [...card.querySelectorAll('button, input')]
                    .filter(el => { const b = box(el); return b.width > 4 && b.height > 4; });
                const collisions = [];
                for (let i = 0; i < controls.length; i++) {
                    for (let j = i + 1; j < controls.length; j++) {
                        const a = controls[i], b = controls[j];
                        if (a.contains(b) || b.contains(a)) continue;
                        const hit = collide(box(a), box(b));
                        if (hit) collisions.push(`${label(a)} × ${label(b)} 重叠 ${hit}`);
                    }
                }

                const c = box(card);
                const outside = [...card.querySelectorAll('button, input')]
                    .filter(el => {
                        const b = box(el);
                        if (b.width === 0 || b.height === 0) return false;
                        return b.left < c.left - 1 || b.right > c.right + 1 || b.top < c.top - 1 || b.bottom > c.bottom + 1;
                    })
                    .map(label);
                return JSON.stringify({ collisions, outside, controlCount: controls.length });
            }
            """);

        using var parsed = JsonDocument.Parse(report);
        var collisions = parsed.RootElement.GetProperty("collisions").EnumerateArray().Select(e => e.GetString()).ToArray();
        var outside = parsed.RootElement.GetProperty("outside").EnumerateArray().Select(e => e.GetString()).ToArray();
        var controlCount = parsed.RootElement.GetProperty("controlCount").GetInt32();

        // 控件数掉了说明选择器或组件结构变了，那时上面两条断言都是空转。
        Assert.True(controlCount >= 8, $"筛选卡里只量到 {controlCount} 个可交互控件，判据已经不成立");
        Assert.True(collisions.Length == 0, string.Join("；", collisions));
        Assert.True(outside.Length == 0, $"跑出筛选卡外：{string.Join("；", outside)}");
    }

    /// <summary>
    /// 表头吸附要给容器一个确定高度才有滚动容器；那个高度写在 app.css 的 --dt-grid-height 里，
    /// 一旦 calc() 被判无效（比如引用了 :root 取不到的变量），变量静默解析为空，
    /// 表格就退回"整页滚"，页头也照样在 —— 只有量出来才知道。
    /// </summary>
    [Fact]
    public async Task ResultGridHasARealScrollHeight()
    {
        await Page.SetViewportSizeAsync(DesktopWidth, DesktopHeight);
        await Page.GotoAsync($"{App.BaseUrl}/query");
        await WaitForAsync(".mud-table-container");

        var measured = await Page.EvaluateAsync<double[]>("""
            () => {
                const variable = getComputedStyle(document.documentElement).getPropertyValue('--dt-grid-height');
                const container = document.querySelector('.mud-table-container');
                return [variable.trim().length === 0 ? -1 : container.getBoundingClientRect().height];
            }
            """);

        Assert.True(measured[0] > 0, "--dt-grid-height 解析为空，表格高度静默失效");
        Assert.True(measured[0] >= 320, $"表格可视高度只有 {measured[0]:0}px，低于约定的 320px 下限");
    }

    /// <summary>
    /// 截图目标：名字、路由、区域选择器（空表示整页）。
    /// CI 上（模式为 off）返回空数据，于是这条用例报"已跳过"而不是假绿。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static TheoryData<string, string, string> GoldenTargets
    {
        get
        {
            var data = new TheoryData<string, string, string>();
            if (!VisualGolden.Enabled)
            {
                return data;
            }

            data.Add("filter-card", "/query", ".dt-filter-card");
            data.Add("denied-panel", "/denied?ReturnUrl=%2Fusers", ".dt-main .mud-paper");
            data.Add("app-shell", "/", ".mud-drawer");
            data.Add("settings-form", "/config/settings", ".dt-main");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenTargets))]
    [SupportedOSPlatform("windows")]   // 见 VisualGolden：System.Drawing 是 Windows-only
    public async Task ScreenshotsMatchTheirBaseline(string target, string path, string selector)
    {
        await Page.SetViewportSizeAsync(DesktopWidth, DesktopHeight);
        await Page.GotoAsync($"{App.BaseUrl}{path}");

        var png = selector.Length == 0
            ? await Page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            })
            : await ScreenshotRegionAsync(selector);

        VisualGolden.Verify(target, png);
    }

    /// <summary>
    /// 截一块区域。用"整页截图 + 裁剪"而不是元素的 ScreenshotAsync：
    /// 预渲染的 DOM 会被 circuit 首次渲染整段换掉，元素句柄在截图途中就脱附了
    /// （报 "Element is not attached to the DOM"，与登录表被抹空是同一个根因）。
    /// 先等包围盒连续两次一致再截，既躲开那次替换，也不会裁到一半的位置。
    /// </summary>
    private async Task<byte[]> ScreenshotRegionAsync(string selector)
    {
        var locator = Page.Locator(selector);
        await Page.EvaluateAsync("() => window.scrollTo(0, 0)");

        var previous = await locator.BoundingBoxAsync();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Page.WaitForTimeoutAsync(120);
            var current = await locator.BoundingBoxAsync();
            if (current is null || previous is null)
            {
                previous = current;
                continue;
            }

            if (Math.Abs(current.X - previous.X) < 1 && Math.Abs(current.Y - previous.Y) < 1
                && Math.Abs(current.Width - previous.Width) < 1 && Math.Abs(current.Height - previous.Height) < 1)
            {
                return await Page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Clip = new Clip
                    {
                        X = current.X,
                        Y = current.Y,
                        Width = current.Width,
                        Height = current.Height
                    },
                    Animations = ScreenshotAnimations.Disabled
                });
            }

            previous = current;
        }

        throw new InvalidOperationException(
            previous is null
                ? $"{selector} 一直量不到包围盒（元素不存在或不可见）"
                : $"{selector} 的包围盒一直不稳定，最后 {previous.Width}x{previous.Height}");
    }

    private async Task<int> HorizontalOverflowAsync()
        => await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

    public static TheoryData<string> RoutesForDesktop()
    {
        var data = new TheoryData<string>();
        foreach (var route in Routes)
        {
            data.Add(route);
        }

        return data;
    }
}
