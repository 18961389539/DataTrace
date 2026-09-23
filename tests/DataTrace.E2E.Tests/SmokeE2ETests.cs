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
        await Page.GotoAsync(App.BaseUrl);

        // 「3 台设备」这个计数只有 SignalR circuit 建好、拿到运行时状态后才会出现。
        await WaitForAsync("text=实时看板");
        await WaitForAsync("text=ST010");

        Assert.Contains("上料工站", await Page.InnerTextAsync("body"));
        Assert.Contains("ST030", await Page.InnerTextAsync("body"));
    }

    [Fact]
    public async Task NavExposesConfigSectionForAdmin()
    {
        await Page.GotoAsync(App.BaseUrl);
        await WaitForAsync("text=实时看板");

        var nav = await Page.InnerTextAsync("nav");

        Assert.Contains("工站配置", nav);
        Assert.Contains("用户", nav);
    }
}
