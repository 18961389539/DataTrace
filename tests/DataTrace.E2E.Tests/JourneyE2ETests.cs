namespace DataTrace.E2E.Tests;

/// <summary>
/// 端到端查询链路：自动跑线产的数据能被查到、能过滤、能打开详情、能导出 CSV。
/// </summary>
/// <remarks>
/// 刻意不调「走完一条线」：服务端的 RunOnePallet 持有整条模拟产线的锁，
/// 用例去停自动跑线再手动走线，会和在跑的那一轮排队互等，慢且不稳。
/// 自动跑线本身每约 2.5 秒产一个托盘，等它出数据比重启产线可靠得多。
/// </remarks>
[Collection("e2e")]
public class JourneyE2ETests : E2ETestBase
{
    public JourneyE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    private Task ClickButtonAsync(string name)
        => Page.GetByRole(AriaRole.Button, new() { Name = name }).ClickAsync();

    /// <summary>
    /// 等第一条采集记录可查。页面只在加载和点「查询」时取数，而首条记录要等模拟器跑完一轮，
    /// 所以这里反复触发查询而不是点一次干等文本——机器忙时点一次会整分钟白等。
    /// </summary>
    private async Task WaitForResultRowsAsync(int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        TimeoutException? last = null;

        while (DateTime.UtcNow < deadline)
        {
            await ClickButtonAsync("查询");
            try
            {
                await WaitBodyContainsAsync("明细", 3000);
                return;
            }
            catch (TimeoutException ex)
            {
                last = ex;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"{timeoutMs}ms 内没有采到可查询的记录。", last);
    }

    [Fact]
    public async Task AutoCollectedDataIsQueryableFilterableExportableAndHasDetail()
    {
        await Page.GotoAsync($"{App.BaseUrl}/query");
        await WaitForAsync("text=数据查询");
        await WaitForResultRowsAsync();
        var rows = Page.Locator("table tbody tr");
        Assert.True(await rows.Filter(new() { HasText = "明细" }).CountAsync() > 0);

        var firstRow = await rows.First.InnerHTMLAsync();
        Assert.Contains("ST0", firstRow);

        await SearchAsync(Page.GetByLabel("托盘码", new() { Exact = true }), "ZZZ-不存在的托盘");
        await WaitBodyContainsAsync("共 0 条");

        // 空结果时 MudTable 仍留一行占位，所以按"结果行"数而不是 <tr> 总数。
        Assert.Equal(0, await rows.Filter(new() { HasText = "明细" }).CountAsync());

        await ClickButtonAsync("重置");
        await SearchAsync(Page.GetByLabel("托盘码", new() { Exact = true }), "P");
        await WaitBodyContainsAsync("明细");
        Assert.True(await rows.Filter(new() { HasText = "明细" }).CountAsync() > 0);

        var download = await Page.RunAndWaitForDownloadAsync(async () =>
        {
            await ClickButtonAsync("导出 CSV");
        });

        Assert.EndsWith(".csv", download.SuggestedFilename);
        await download.DeleteAsync();

        await rows.First.ClickAsync();
        await WaitForAsync("text=返回查询");

        Assert.Matches("/query/\\d{6}/\\d+", new Uri(Page.Url).PathAndQuery);
        // 详情是异步取数的：骨架屏上就有"返回查询"链接，等它出现就立刻读 body
        // 会读到还没渲染记录的壳，于是"结果码"时有时无。等记录本身渲染出来再断言。
        await WaitBodyContainsAsync("结果码");
        var detail = await Page.InnerTextAsync("body");
        // 只要求详情真的把这条记录渲染出来了：判定可能是 OK 也可能是 NG
        // （仿真器按比例造不良），钉死"最新一条一定合格"会按那个比例随机红。
        Assert.True(
            detail.Contains("OK 合格") || detail.Contains("NG 不合格") || detail.Contains("未判定"),
            "详情页没有渲染判定结论");
    }

    [Fact]
    public async Task SimulatePageKeepsThreeStationTriggerButtons()
    {
        await Page.GotoAsync($"{App.BaseUrl}/simulate");
        await WaitForAsync("text=PLC 仿真");

        var labels = await Page.Locator("button").Filter(new() { HasText = "触发" }).AllInnerTextsAsync();

        Assert.Equal(
            new[] { "触发 ST010", "触发 ST020", "触发 ST030" },
            labels.Select(x => x.Trim()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SimulatorReportsProgressWhileRunning()
    {
        await Page.GotoAsync($"{App.BaseUrl}/simulate");
        await WaitForAsync("text=PLC 仿真");

        // 自动跑线在跑时，状态卡要随 SignalR 推送刷新出累计计数，这是 circuit 活着的直接证据。
        await Page.WaitForFunctionAsync(
            """
            () => [...document.querySelectorAll('.dt-metric-value')]
                    .some(el => /^[1-9]\d*\s*\/\s*\d+$/.test(el.textContent.trim()))
            """);

        var body = await Page.InnerTextAsync("body");
        Assert.Contains("当前托盘", body);
    }
}
