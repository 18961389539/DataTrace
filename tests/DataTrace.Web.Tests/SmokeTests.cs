using DataTrace.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Web.Tests;

/// <summary>冒烟：确认 bUnit 能承载本应用的 Blazor 组件与 MudBlazor 服务。</summary>
public class SmokeTests : WebTestBase
{
    [Fact]
    public void RendersLoginPage()
    {
        var cut = Context.RenderComponent<Login>();

        Assert.Contains("登录 DataTrace", cut.Markup);
        Assert.Contains("login-btn", cut.Markup);

        // 原生密码框不带 Mud 的 adornment，显隐开关得自己给：缺了它现场只能盲敲，
        // 少可访问名时读屏只会念"按钮"。开关靠 js/datatrace.js 的事件委托驱动。
        Assert.Contains("data-dt-pass-toggle", cut.Markup);
        Assert.Contains("aria-label=\"显示密码\"", cut.Markup);

        // 记住我：勾选框的 name 必须与登录端点读的字段一致——写成 Remember 之类就永远对不上，
        // 表现是"勾了也没用"，界面与日志都不报错。默认不勾，公共终端不会被下一位接着用。
        var remember = cut.Find("input[name=RememberMe]");
        Assert.Equal("true", remember.GetAttribute("value"));
        Assert.Contains("记住我", cut.Markup);
        Assert.False(remember.HasAttribute("checked"));
    }

    /// <summary>登录失败回跳会带 remember=1，勾选状态得跟着回来：重输一次就得重新勾，
    /// 用户多半不会再勾，下次开浏览器被登出时还以为自己勾过。</summary>
    [Fact]
    public void LoginPageRestoresRememberMeFromTheQuery()
    {
        // 查询参数只能从 NavigationManager 进来：bUnit 会直接拒绝把它当普通参数传（见其异常提示）。
        Context.Services.GetRequiredService<NavigationManager>().NavigateTo("/login?remember=1");

        var cut = Context.RenderComponent<Login>();

        Assert.True(cut.Find("input[name=RememberMe]").HasAttribute("checked"));
    }
}
