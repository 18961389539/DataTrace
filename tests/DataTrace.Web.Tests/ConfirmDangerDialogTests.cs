using AngleSharp.Dom;
using DataTrace.Web.Components.Dialogs;
using DataTrace.Web.Services;
using DataTrace.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;

namespace DataTrace.Web.Tests;

/// <summary>
/// 危险操作的第二道门：确认按钮在勾选「我了解…」之前必须点不动。
/// </summary>
/// <remarks>
/// 走真对话框路径（真 DialogService + MudDialogProvider），否则只能验证页面调用了几次，
/// 验证不了"没勾选就点不了"这条约束真的生效。
/// </remarks>
public class ConfirmDangerDialogTests : WebTestBase
{
    /// <summary>打开真对话框的宿主，并交出对话框服务（后续用 ConfirmDangerAsync 走完整链路）。</summary>
    private (IRenderedComponent<MudDialogProvider> Provider, IDialogService Dialogs) OpenHost()
    {
        Context.Services.AddSingleton<IDialogService, DialogService>();
        RenderPopoverHost();
        var provider = Context.RenderComponent<MudDialogProvider>();
        return (provider, Context.Services.GetRequiredService<IDialogService>());
    }

    private static IElement Button(IRenderedFragment provider, string text)
    {
        var button = provider.FindAll("button").SingleOrDefault(b => b.TextContent.Contains(text));
        Assert.NotNull(button);
        return button!;
    }

    [Fact]
    public async Task Confirming_requires_the_acknowledgement_to_be_checked_first()
    {
        var (provider, dialogs) = OpenHost();

        var confirmTask = dialogs.ConfirmDangerAsync(
            "删除用户",
            "确定删除用户「u1」？该账号将无法再登录，且操作不可恢复。",
            acknowledge: "我了解该账号将无法再登录，且无法恢复",
            yesText: "删除",
            cancelText: "取消");

        provider.WaitForAssertion(() => Assert.Contains("无法恢复", provider.Markup));

        // 还没勾选：确认按钮必须是 disabled 的，否则"一路回车"照样能把删除确认掉。
        Assert.True(Button(provider, "删除").HasAttribute("disabled"), "未勾选就把确认按钮放开了");

        provider.Find("input[type=checkbox]").Change(true);
        provider.Render();

        Assert.False(Button(provider, "删除").HasAttribute("disabled"), "勾选之后确认按钮仍点不动");

        Button(provider, "删除").Click();

        Assert.True(await confirmTask, "勾选并确认后应返回 true");
    }

    [Fact]
    public async Task Cancelling_returns_false_so_the_page_writes_nothing()
    {
        var (provider, dialogs) = OpenHost();

        var confirmTask = dialogs.ConfirmDangerAsync(
            "删除用户",
            "确定删除用户「u1」？",
            acknowledge: "我了解该账号将无法再登录，且无法恢复");

        provider.WaitForAssertion(() => Assert.Contains("无法恢复", provider.Markup));

        Button(provider, "取消").Click();

        Assert.False(await confirmTask, "取消应返回 false");
    }
}