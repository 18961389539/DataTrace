using DataTrace.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>
/// 权限不足页。角色越权时用户必须知道"是哪一页、为什么"，
/// 而不是被无声地扔回看板 —— 那正是 AccessDeniedPath 指向 /login 时的行为。
/// </summary>
public class DeniedPanelTests
{
    private static IRenderedComponent<DeniedPanel> Render(string? userName = null, string? requestedPath = null)
    {
        var ctx = new TestContext();
        ctx.Services.AddMudServices();

        return ctx.RenderComponent<DeniedPanel>(parameters => parameters
            .Add(p => p.UserName, userName)
            .Add(p => p.RequestedPath, requestedPath));
    }

    [Fact]
    public void Names_the_account_and_the_page_that_was_blocked()
    {
        var text = Render("engineer", "users").Find(".mud-paper").TextContent;

        Assert.Contains("没有权限访问该页面", text);
        Assert.Contains("当前账号 engineer 的角色不足", text);
        Assert.Contains("你想打开的「用户」", text);
    }

    [Fact]
    public void DoesNotInventAPageWhenTheBlockedUrlIsUnknown()
    {
        var text = Render("engineer", "bogus").Find(".mud-paper").TextContent;

        Assert.DoesNotContain("你想打开的", text);
    }

    [Fact]
    public void DoesNotClaimTheDashboardWasRequested_WhenNoReturnUrlWasCarried()
    {
        // Cookie 认证不总是带得上 ReturnUrl；这时不能把"根路径"当成用户想去的地方。
        var text = Render("engineer", null).Find(".mud-paper").TextContent;

        Assert.DoesNotContain("你想打开的", text);
        Assert.DoesNotContain("实时看板", text.Replace("回到实时看板", ""));
    }

    [Fact]
    public void StillReads_sensiblyWithoutAUserName()
    {
        var text = Render(requestedPath: "users").Find(".mud-paper").TextContent;

        Assert.DoesNotContain("当前账号 的角色不足", text);
        Assert.Contains("你想打开的「用户」", text);
    }
}
