using Microsoft.Playwright;

namespace DataTrace.E2E.Tests;

/// <summary>
/// 可访问名普查。图标按钮没有可见文字，读屏只能靠 aria-label / 关联 label 取名；
/// 名字缺失或写错（比如 AriaLabel 落成惰性的 arialabel）在页面上完全看不出来。
/// 这里用浏览器的真实可访问名计算，而不是猜属性写对没对。
/// </summary>
[Collection("e2e")]
public class AccessibleNameE2ETests : E2ETestBase
{
    public AccessibleNameE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    private static readonly string[] Routes =
    [
        "/", "/query", "/reports", "/curve-baseline", "/logs",
        "/config/plc", "/config/stations", "/config/recipes",
        "/config/settings", "/simulate", "/users"
    ];

    [Theory]
    [MemberData(nameof(RouteCases))]
    public async Task EveryVisibleControlIsNamed(string path)
    {
        await Page.GotoAsync($"{App.BaseUrl}{path}");
        await WaitForAsync(".mud-layout");

        // 等 SignalR circuit 真的接管：本应用的"导航后把焦点移到页标题"只在接管之后才会发生，
        // 所以它是个现成的就绪信号。等元素存在会读到只有静态预渲染的一半页面。
        await Page.WaitForFunctionAsync(
            "() => document.activeElement === document.querySelector('h5')",
            arg: null,
            new() { Timeout = 20000 });

        var offenders = await Page.EvaluateAsync<string[][]>("""
            () => {
                const sel = 'button, a[href], input:not([type=hidden]), select, [role=button]';
                const out = [];
                for (const el of document.querySelectorAll(sel)) {
                    const r = el.getBoundingClientRect();
                    if (r.width < 2 || r.height < 2) continue;
                    if (el.disabled) continue;
                    const text = (el.textContent || '').trim();
                    const aria = el.getAttribute('aria-label');
                    const labelled = el.getAttribute('aria-labelledby');
                    const id = el.id;
                    const boundLabel = id ? !!document.querySelector(`label[for="${CSS.escape(id)}"]`) : false;
                    // 被 <label> 包住也算有名字（隐式关联）—— MudSwitch 就是这样，
                    // 只查 label[for] 会把每个开关都误报成未命名。
                    const implicitLabel = !!el.closest('label');
                    if (!(aria || labelled || boundLabel || implicitLabel || text)) {
                        out.push([el.tagName.toLowerCase(), (el.className || '').slice(0, 46),
                                  el.type || '']);
                    }
                }
                return out;
            }
            """);

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task IconOnlyButtonsResolveANameInTheRealA11yTree()
    {
        // 只有图标、没有可见文字的按钮必须靠 aria-label 拿到名字。
        // 这条专门用来抓 "AriaLabel 渲染成惰性 arialabel" 那一类问题。
        // 用 GetByRole(Name=…) 而不是读属性：role + name 的匹配过程就是浏览器在算可访问名。
        await Page.GotoAsync($"{App.BaseUrl}/query");
        await WaitForAsync(".mud-layout");

        Assert.True(
            await Page.GetByRole(AriaRole.Button, new() { Name = "复制托盘码" }).CountAsync() > 0,
            "复制按钮算不出可访问名");

        Assert.True(
            await Page.GetByRole(AriaRole.Button, new() { Name = "展开或收起菜单" }).CountAsync() > 0,
            "菜单按钮算不出可访问名");

        // 反向哨兵：惰性的 arialabel 属性不该再出现在页面上。
        Assert.Equal(0, await Page.EvaluateAsync<int>("() => document.querySelectorAll('[arialabel]').length"));
    }

    [Fact]
    public async Task PasswordVisibilityToggleIsNamed()
    {
        // 密码框右侧那只眼睛图标是 MudTextField 的 adornment 按钮，没有可见文字。
        // 它只能通过 AdornmentAriaLabel 命名 —— 漏掉时读屏只会念"按钮"。
        await Page.GotoAsync($"{App.BaseUrl}/users");
        await WaitForAsync(".mud-layout");

        var toggles = Page.Locator(".mud-input-adornment-icon-button");
        Assert.True(await toggles.CountAsync() > 0, "页面上找不到密码显隐按钮，用例失去意义");

        Assert.True(
            await Page.GetByRole(AriaRole.Button, new() { Name = "显示密码" }).CountAsync() > 0,
            "密码显隐按钮没有可访问名");
    }

    public static TheoryData<string> RouteCases()
    {
        var data = new TheoryData<string>();
        foreach (var route in Routes)
        {
            data.Add(route);
        }

        return data;
    }
}
