using DataTrace.Web.Components.Pages;
using DataTrace.Web.Services;
using Microsoft.AspNetCore.Http;

namespace DataTrace.Web.Tests;

/// <summary>
/// 错误页把中间件配好的回报码显示出来。
/// </summary>
/// <remarks>
/// 这一页匿名可达，所以除了"该显示时要显示"，同样重要的是"塞进来的任意文本不能原样渲染"。
/// </remarks>
public class ErrorPageTests : WebTestBase
{
    [Fact]
    public void Shows_the_code_stashed_by_the_middleware()
    {
        var http = new DefaultHttpContext();
        http.Items[ExceptionTraceMiddleware.CodeItemKey] = "AB12CD34";

        var cut = RenderError(http);

        Assert.Contains("AB12CD34", cut.Markup);
    }

    [Fact]
    public void Shows_nothing_when_visited_without_an_exception()
    {
        var cut = RenderError(new DefaultHttpContext());

        // 直接访问 /Error（没有异常在身）不该凭空多出一个码 —— 那个码在日志里搜不到，
        // 只会把工程师带向一条不存在的记录。
        Assert.Empty(cut.FindAll(".dt-error-code"));
    }

    [Fact]
    public void Never_renders_a_value_that_is_not_our_code()
    {
        var http = new DefaultHttpContext();
        http.Items[ExceptionTraceMiddleware.CodeItemKey] = "<img src=x onerror=alert(1)>";

        var cut = RenderError(http);

        Assert.Empty(cut.FindAll(".dt-error-code"));
        Assert.DoesNotContain("onerror", cut.Markup);
    }

    private IRenderedComponent<Error> RenderError(HttpContext http)
        => Context.RenderComponent<Error>(parameters => parameters.AddCascadingValue<HttpContext>(http));
}
