namespace DataTrace.E2E.Tests;

/// <summary>每个主路由都能在真实浏览器里打开，且不出现 Blazor 的错误条。</summary>
[Collection("e2e")]
public class RouteE2ETests : E2ETestBase
{
    public RouteE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    [Theory]
    [InlineData("/", "实时看板")]
    [InlineData("/logs", "审计日志")]
    [InlineData("/query", "数据查询")]
    [InlineData("/reports", "报表")]
    [InlineData("/curve-baseline", "波形基线")]
    [InlineData("/config/plc", "PLC")]
    [InlineData("/config/stations", "工站")]
    [InlineData("/config/recipes", "产品型号")]
    [InlineData("/config/settings", "系统设置")]
    [InlineData("/simulate", "PLC 仿真")]
    [InlineData("/users", "用户")]
    public async Task RouteRendersWithoutErrorBanner(string path, string expectedText)
    {
        await Page.GotoAsync($"{App.BaseUrl}{path}");
        await WaitForAsync($".mud-layout, .dt-page");

        var body = await Page.InnerTextAsync("body");

        Assert.Contains(expectedText, body);
        Assert.Equal(0, await Page.Locator(".mud-error-banner").CountAsync());
    }
}
