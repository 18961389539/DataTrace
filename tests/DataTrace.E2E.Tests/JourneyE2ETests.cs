using System.Text.RegularExpressions;

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

    private Task ClickButtonAsync(string name, bool exact = false)
        => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = exact }).ClickAsync();

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
            await ClickButtonAsync("查询", exact: true);
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
        await OpenAsync("/query");
        await WaitForAsync("text=数据查询");
        await WaitForResultRowsAsync();
        var rows = Page.Locator("table tbody tr");
        Assert.True(await rows.Filter(new() { HasText = "明细" }).CountAsync() > 0);

        var firstRow = await rows.First.InnerHTMLAsync();
        Assert.Contains("ST0", firstRow);

        // circuit 接手前发出的回车会被丢掉（什么都没发生），用重试兜住这个窗口。
        await ActUntilAsync(
            () => SearchAsync(Page.GetByLabel("托盘码", new() { Exact = true }), "ZZZ-不存在的托盘"),
            "共 0 条",
            "提交托盘码查询");

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

        // 点行同样走服务端事件：circuit 接手前发出的点击会被丢掉（什么都没发生）。
        // 用重试兜住那个窗口 —— 与上面提交查询同一套做法。
        await ActUntilAsync(() => rows.First.ClickAsync(), "返回查询", "点开明细");

        // 明细是 Blazor 的增强导航（History API 改地址），DOM 换新与地址栏更新不是同一拍：
        // 刚看到「返回查询」就读 Page.Url，可能还停在列表页的地址上。等路径真落到明细页再断言。
        await Page.WaitForURLAsync(new Regex("/query/\\d{6}/\\d+"));

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

    /// <summary>
    /// 托盘全链路时序：追溯视图要给出逐站间隔、全链路时长，以及链路两头的会话结束与 MES 推送。
    /// </summary>
    /// <remarks>
    /// 用真实记录跑。纯函数单测只保证间隔算得对，保证不了它有没有被渲染出来；
    /// 而"时序"这套东西的价值全在渲染上，所以这段必须过一遍真浏览器。
    /// 单个流水号可能只有一站（在制的一半），那就换下一个 —— 往列表里连试几个，
    /// 只要有一条走到过第二站就足以验证间隔。
    /// </remarks>
    [Fact]
    public async Task PalletTraceShowsTimelineWithSegmentDurationsAndEndpoints()
    {
        await OpenAsync("/query");
        await WaitForAsync("text=数据查询");
        await WaitForResultRowsAsync();

        // 列序：时间 / 流水号 / 托盘码 / 工站 / 型号 / 判定 / 结果码 / 耗时 / 操作。
        var rows = Page.Locator("table tbody tr").Filter(new() { HasText = "明细" });
        var candidates = new List<string>();
        var count = Math.Min(6, await rows.CountAsync());
        for (var i = 0; i < count; i++)
        {
            var serial = (await rows.Nth(i).Locator("td").Nth(1).InnerTextAsync()).Trim();
            if (!string.IsNullOrWhiteSpace(serial) && !candidates.Contains(serial))
            {
                candidates.Add(serial);
            }
        }

        Assert.NotEmpty(candidates);

        var sawInterval = false;
        foreach (var serial in candidates)
        {
            await OpenAsync($"/query?view=trace&trace={Uri.EscapeDataString(serial)}");
            // 履历标题只在真的查到会话时才出现；等它出来说明追溯数据已经到位。
            await WaitBodyContainsAsync("工站履历", 30000);

            var body = await Page.InnerTextAsync("body");
            Assert.Contains("全链路", body);
            Assert.Contains("会话结束", body);
            Assert.Contains("MES 推送", body);

            if (body.Contains("间隔 +"))
            {
                sawInterval = true;
                break;
            }
        }

        Assert.True(sawInterval, $"连续 {candidates.Count} 个流水号的追溯视图都没有渲染出站间间隔");
    }

    [Fact]
    public async Task SimulatePageShowsSixStationTriggerButtons()
    {
        await OpenAsync("/simulate");
        await WaitForAsync("text=PLC 仿真");

        var labels = await Page.Locator("button").Filter(new() { HasText = "触发" }).AllInnerTextsAsync();

        Assert.Equal(
            new[] { "触发 ST010", "触发 ST020", "触发 ST030", "触发 ST040", "触发 ST050", "触发 ST060" },
            labels.Select(x => x.Trim()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SimulatorReportsProgressWhileRunning()
    {
        await OpenAsync("/simulate");
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

    /// <summary>
    /// 工站试读：在真数据下点一次「试读一次」，要真读回握手与点位的值。
    /// </summary>
    /// <remarks>
    /// bUnit 只能证明"点了会渲染结果面板"，证明不了面板里的值真是从 PLC 读回来的。
    /// 开发态跑着内置模拟 PLC，所以这里能要求它读到值 —— 只出一句错误提示就说明链路没通。
    /// </remarks>
    [Fact]
    public async Task Station_trial_reads_real_values_from_the_line()
    {
        await OpenAsync("/config/stations");
        await WaitForCircuitReadyAsync();
        await WaitBodyContainsAsync("工站配置");

        await ClickButtonAsync("试读一次");

        await WaitBodyContainsAsync("试读结果", 30000);
        await WaitBodyContainsAsync("触发", 5000);
        await WaitBodyContainsAsync("托盘码", 5000);
    }
}
