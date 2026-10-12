namespace DataTrace.E2E.Tests;

/// <summary>冒烟：应用能在真实浏览器里起来，并且 Blazor circuit 通了。</summary>
[Collection("e2e")]
public class SmokeE2ETests : E2ETestBase
{
    public SmokeE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    [Fact]
    public async Task AppStartsWithoutBrowser()
    {
        await App.Started;

        using var http = new System.Net.Http.HttpClient();
        var home = await http.GetStringAsync($"{App.BaseUrl}/");

        // 夹具跑在 Development 下，中间件会把未登录请求自动登成 admin，
        // 所以首页直接渲染看板而不是登录页（生产环境必须走 /login）。
        Assert.Contains("实时看板", home);
    }

    [Fact]
    public async Task DashboardRendersSeededLineOverRealCircuit()
    {
        await OpenAsync("");

        // 工站计数只有 SignalR circuit 建好、拿到运行时状态后才会出现。
        await WaitForAsync("text=实时看板");
        await WaitForAsync("text=6 台工站");

        Assert.Contains("上料工站", await Page.InnerTextAsync("body"));
        Assert.Contains("ST060", await Page.InnerTextAsync("body"));
    }

    [Fact]
    public async Task StationCardLongTitlesWrapWithoutOverflow()
    {
        await OpenAsync("");
        var card = await WaitForAsync(".dash-station");

        var layout = await card.EvaluateAsync<bool[]>("""
            element => {
                const title = element.querySelector(".dt-station-title");
                title.textContent = "超长工站标题".repeat(12);
                const titleStyle = getComputedStyle(title);
                const lineHeight = Number.parseFloat(titleStyle.lineHeight);
                const titleRect = title.getBoundingClientRect();
                const cardRect = element.getBoundingClientRect();
                return [
                    titleStyle.whiteSpace === "normal",
                    titleStyle.overflowWrap === "anywhere",
                    titleRect.height > lineHeight,
                    title.scrollWidth <= title.clientWidth,
                    titleRect.right <= cardRect.right
                ];
            }
            """);

        Assert.All(layout, condition => Assert.True(condition));
    }

    [Fact]
    public async Task StationCardKeepsDetailsInHeaderAndShowsConfiguredCurvesWithoutEmptyRegions()
    {
        await OpenAsync("");
        await WaitForAsync(".dash-station");
        await WaitForAsync(".dt-station-detail-link");
        await WaitForAsync(".dt-station-curves .dt-station-curve");

        var detailsLink = Page.Locator(".dt-station-detail-link").First;
        Assert.Equal("查看本站明细", await detailsLink.GetAttributeAsync("aria-label"));
        Assert.True(await detailsLink.EvaluateAsync<bool>(
            "element => !!element.closest('.dt-station-header')"));

        var curveLayoutIsValid = await Page.Locator(".dash-station").EvaluateAllAsync<bool>("""
            cards => cards.every(card => {
                const region = card.querySelector('.dt-station-curves');
                const curves = card.querySelectorAll('.dt-station-curve');
                // 未配置曲线的卡片不应留下空图表区域；有区域时，所有曲线定义都要有可见卡片。
                return region === null
                    ? curves.length === 0
                    : curves.length > 0
                        && region.getAttribute('aria-label') === '全部采集曲线'
                        && [...curves].every(curve => {
                            const name = curve.getAttribute('aria-label')?.replace(/^曲线 /, '');
                            return !!name && curve.innerText.includes(name);
                        });
            })
            """);

        Assert.True(curveLayoutIsValid);
    }

    [Fact]
    public async Task NavExposesConfigSectionForAdmin()
    {
        await OpenAsync("");
        await WaitForAsync("text=实时看板");

        var nav = await Page.InnerTextAsync("nav");

        Assert.Contains("工站配置", nav);
        Assert.Contains("用户", nav);
    }
}
