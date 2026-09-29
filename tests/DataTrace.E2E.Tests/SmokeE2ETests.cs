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
    public async Task StationCardsUseContentHeightAndEllipsizeLongTitles()
    {
        await OpenAsync("");
        var card = await WaitForAsync(".dash-station");

        var styles = await card.EvaluateAsync<string[]>("""
            element => {
                const title = element.querySelector(".dt-station-title");
                const cardStyle = getComputedStyle(element);
                const titleStyle = getComputedStyle(title);
                return [
                    cardStyle.minHeight,
                    titleStyle.whiteSpace,
                    titleStyle.textOverflow,
                    titleStyle.overflow
                ];
            }
            """);

        Assert.Equal("0px", styles[0]);
        Assert.Equal("nowrap", styles[1]);
        Assert.Equal("ellipsis", styles[2]);
        Assert.Equal("hidden", styles[3]);
    }

    [Fact]
    public async Task StationCardDetailsAndCurveNameUseCompactRegions()
    {
        await OpenAsync("");
        await WaitForAsync(".dash-station");
        await WaitForAsync(".dt-station-detail-link");
        await WaitForAsync(".dt-station-curve-label");

        var detailsLink = Page.Locator(".dt-station-detail-link").First;
        Assert.Equal("查看本站明细", await detailsLink.GetAttributeAsync("aria-label"));
        Assert.True(await detailsLink.EvaluateAsync<bool>(
            "element => !!element.closest('.dt-station-header')"));

        var curveLabel = Page.Locator(".dt-station-curve-label").First;
        var curveLayout = await curveLabel.EvaluateAsync<bool[]>("""
            element => [
                getComputedStyle(element.parentElement).position === 'relative',
                getComputedStyle(element).position === 'absolute',
                element.title === element.textContent
            ]
            """);

        Assert.All(curveLayout, condition => Assert.True(condition));
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
