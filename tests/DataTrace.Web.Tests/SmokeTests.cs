using DataTrace.Web.Components.Pages;

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
    }
}
