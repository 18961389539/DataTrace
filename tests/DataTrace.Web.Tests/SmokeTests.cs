using DataTrace.Web.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace DataTrace.Web.Tests;

/// <summary>冒烟：确认 bUnit 能承载本应用的 Blazor 组件与 MudBlazor 服务。</summary>
public class SmokeTests
{
    [Fact]
    public void RendersLoginPage()
    {
        using var ctx = new TestContext();
        ctx.Services.AddMudServices();

        var cut = ctx.RenderComponent<Login>();

        Assert.Contains("登录 DataTrace", cut.Markup);
        Assert.Contains("login-btn", cut.Markup);
    }
}
