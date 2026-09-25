using Xunit;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 概念详解（ⓘ）在真浏览器里的两条路径：鼠标悬停、键盘聚焦。
/// </summary>
/// <remarks>
/// 用属性选择器而不是 GetByRole(Name=…)：Playwright 的 Name 默认是子串匹配，
/// 报表页同时有「说明：直通率」与「说明：平均直通率」两枚 ⓘ，用名字定位会命中两个元素。
/// </remarks>
[Collection("e2e")]
public sealed class InfoHintE2ETests : E2ETestBase
{
    public InfoHintE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    [Fact]
    public async Task Hovering_a_hint_shows_the_three_part_explanation()
    {
        await Page.GotoAsync($"{App.BaseUrl}/reports");
        await WaitForAsync(".mud-layout");
        await WaitForCircuitReadyAsync();

        var hint = Page.Locator("th button[aria-label='说明：直通率']");
        Assert.Equal(1, await hint.CountAsync());

        await hint.HoverAsync();

        // 页面上每枚 ⓘ 都带一个 .mud-tooltip 节点（内容懒渲染，未显示时是空的），
        // 所以要找"哪一个装了三段文案"，而不是取第一个。
        // 三段里最有代表性的两句：口径（怎么算）与影响（会不会阻断生产）。
        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('.mud-tooltip')].some(t => {
                const text = t.innerText || '';
                return text.includes('不进分子也不进分母') && text.includes('不阻断生产');
            })
            """);

        var text = await Page.EvaluateAsync<string>(
            "() => [...document.querySelectorAll('.mud-tooltip')].map(t => t.innerText || '').join(' ')");
        Assert.Contains("直通率", text);
    }

    [Fact]
    public async Task Every_hint_on_the_key_pages_opens_with_real_content()
    {
        // 单测只能保证文案库本身没问题；这条负责"界面上每一枚 ⓘ 真的挂着内容"——
        // 传错 Topic、条件渲染写错、Extra 路径漏了正文，都只会在浏览器里露出来。
        foreach (var path in new[]
                 {
                     "/", "/query", "/reports", "/curve-baseline", "/logs",
                     "/config/plc", "/config/stations", "/config/recipes",
                     "/config/settings", "/simulate", "/users"
                 })
        {
            await Page.GotoAsync($"{App.BaseUrl}{path}");
            await WaitForAsync(".mud-layout");
            await WaitForCircuitReadyAsync();

            var labels = await Page.EvaluateAsync<string[]>(
                "() => [...document.querySelectorAll('button[aria-label^=\"说明：\"]')]"
                + ".map(b => b.getAttribute('aria-label'))");

            Assert.True(labels.Length > 0, $"{path} 上一枚 ⓘ 都没有，用例失去意义");

            // 同名多枚（例如用户页每行都有的「删除用户」）验一次就够。
            foreach (var label in labels.Distinct())
            {
                var text = await Page.EvaluateAsync<string>(
                    """
                    (label) => {
                        const button = document.querySelector(`button[aria-label='${label}']`);
                        const id = button?.getAttribute('aria-describedby');
                        return id ? (document.getElementById(id)?.textContent ?? '') : '';
                    }
                    """,
                    label);

                var concept = label.Replace("说明：", "");
                Assert.False(string.IsNullOrWhiteSpace(text), $"{path} 的「{label}」没有可读正文");
                Assert.Contains(concept, text);
            }

            // 内容之外再验一次交互：每页第一枚悬停后真的弹出浮层（且不是空壳）。
            await Page.Locator($"button[aria-label='{labels[0]}']").First.HoverAsync();
            await Page.WaitForFunctionAsync(
                "() => [...document.querySelectorAll('.mud-tooltip')].some(t => (t.innerText || '').trim().length > 10)");

            // 页头的「本页说明」必须涵盖这一页上的每一枚 ⓘ：
            // 两处口径走岔时，新人会从同一个页面拿到两个不一致的清单。
            var helpButton = Page.Locator("button[aria-label='本页说明']");
            Assert.Equal(1, await helpButton.CountAsync());
            await helpButton.ClickAsync();
            await Page.WaitForFunctionAsync(
                "t => document.body.innerText.includes(t)",
                labels[0].Replace("说明：", ""));

            var overview = await Page.EvaluateAsync<string>("() => document.body.innerText");
            foreach (var label in labels.Distinct())
            {
                var concept = label.Replace("说明：", "");
                Assert.Contains(concept, overview);
            }
        }
    }

    [Fact]
    public async Task A_hint_can_be_read_with_the_keyboard_alone()
    {
        await Page.GotoAsync($"{App.BaseUrl}/reports");
        await WaitForAsync(".mud-layout");
        await WaitForCircuitReadyAsync();

        // 真按钮才吃得到 focus：ⓘ 若是装饰图标，键盘用户就拿不到任何解释。
        // 这里直接 focus 而不是按 Tab —— Tab 的次数会随页面上的控件增减而变。
        await Page.EvaluateAsync(
            "() => document.querySelector(\"button[aria-label='说明：直通率']\").focus()");

        await Page.WaitForFunctionAsync("""
            () => [...document.querySelectorAll('.mud-tooltip')]
                    .some(t => (t.innerText || '').includes('不进分子也不进分母'))
            """);
    }
}
