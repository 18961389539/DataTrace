using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace DataTrace.E2E.Tests;

[Collection("e2e")]
public sealed class QualityIncidentWorkflowE2ETests : E2ETestBase
{
    public QualityIncidentWorkflowE2ETests(WebAppFixture app, BrowserFixture browser)
        : base(app, browser)
    {
    }

    [Fact]
    public async Task QualityIncidentCanBeCreatedWorkedVerifiedAndClosed()
    {
        var marker = "E2E-QUALITY-" + Guid.NewGuid().ToString("N")[..8];

        await OpenAsync("/quality/incidents/new");
        await Page.GetByRole(AriaRole.Button, new() { Name = "搜索记录", Exact = true }).ClickAsync();

        var sourceRows = Page.Locator("table tbody tr").Filter(new()
        {
            Has = Page.GetByRole(AriaRole.Button, new() { Name = "选择此记录", Exact = true })
        });
        var sourceDeadline = DateTime.UtcNow.AddSeconds(60);
        while (await sourceRows.CountAsync() == 0 && DateTime.UtcNow < sourceDeadline)
        {
            await Task.Delay(1000);
            await Page.GetByRole(AriaRole.Button, new() { Name = "搜索记录", Exact = true }).ClickAsync();
        }

        Assert.True(await sourceRows.CountAsync() > 0, "模拟产线在 60 秒内没有生成可用源记录。");
        var sourceBusinessId = (await sourceRows.First.Locator("td").Nth(1).InnerTextAsync()).Trim();
        Assert.False(string.IsNullOrWhiteSpace(sourceBusinessId), "源记录行没有业务标识。");
        await sourceRows.First.GetByRole(AriaRole.Button, new() { Name = "选择此记录", Exact = true }).ClickAsync();

        await Page.GetByLabel("异常标题", new() { Exact = true }).FillAsync(marker);
        await Page.GetByLabel("异常描述", new() { Exact = true }).FillAsync("端到端测试创建的质量异常描述。");
        await Page.GetByRole(AriaRole.Button, new() { Name = "创建异常单", Exact = true }).ClickAsync();
        await Page.WaitForURLAsync(new Regex(@"/quality/incidents/\d+$"));
        await WaitBodyContainsAsync(marker);

        await Page.GetByRole(AriaRole.Button, new() { Name = "接手处理", Exact = true }).ClickAsync();
        await WaitBodyContainsAsync("处理中");
        await Page.GetByLabel("本次处置措施", new() { Exact = true }).FillAsync("隔离测试批次并复核工艺参数。");
        await Page.GetByRole(AriaRole.Button, new() { Name = "记录措施", Exact = true }).ClickAsync();
        await WaitBodyContainsAsync("隔离测试批次并复核工艺参数。");

        await Page.GetByRole(AriaRole.Button, new() { Name = "搜索验证记录", Exact = true }).ClickAsync();
        var verificationRows = Page.Locator("table tbody tr").Filter(new()
        {
            Has = Page.GetByRole(AriaRole.Button, new() { Name = "选择", Exact = true })
        });
        ILocator? verificationRow = null;
        var verificationDeadline = DateTime.UtcNow.AddSeconds(60);
        while (verificationRow is null && DateTime.UtcNow < verificationDeadline)
        {
            for (var index = 0; index < await verificationRows.CountAsync(); index++)
            {
                var row = verificationRows.Nth(index);
                var businessId = (await row.Locator("td").Nth(1).InnerTextAsync()).Trim();
                if (!string.Equals(businessId, sourceBusinessId, StringComparison.Ordinal))
                {
                    verificationRow = row;
                    break;
                }
            }

            if (verificationRow is null)
            {
                await Task.Delay(1000);
                await Page.GetByRole(AriaRole.Button, new() { Name = "搜索验证记录", Exact = true }).ClickAsync();
            }
        }

        Assert.True(verificationRow is not null, "60 秒内没有生成独立的验证记录。");
        await verificationRow!.GetByRole(AriaRole.Button, new() { Name = "选择", Exact = true }).ClickAsync();

        var causeCategory = Page.GetByLabel("原因分类", new() { Exact = true });
        await causeCategory.ScrollIntoViewIfNeededAsync();
        await causeCategory.ClickAsync(new() { Force = true });
        await Page.GetByText("设备故障", new() { Exact = true }).Last.ClickAsync();
        await Page.GetByLabel("最终处置措施", new() { Exact = true }).FillAsync("更换易损件并恢复标准工艺参数。");
        await Page.GetByLabel("验证结论", new() { Exact = true }).FillAsync("独立复测记录确认结果稳定。");
        await Page.GetByRole(AriaRole.Button, new() { Name = "提交复核", Exact = true }).ClickAsync();
        await WaitBodyContainsAsync("待复核");

        await Page.GetByLabel("复核意见或退回原因", new() { Exact = true }).FillAsync("复核通过，验证证据与处置记录完整。");
        await Page.GetByRole(AriaRole.Button, new() { Name = "通过并关闭", Exact = true }).ClickAsync();
        await WaitBodyContainsAsync("已关闭");
        await WaitBodyContainsAsync("独立复测记录确认结果稳定。");
    }
}
