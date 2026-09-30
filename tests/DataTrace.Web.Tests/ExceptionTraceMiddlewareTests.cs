using DataTrace.Shared;
using DataTrace.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DataTrace.Web.Tests;

/// <summary>
/// 未处理异常的回报码中间件。
/// </summary>
/// <remarks>
/// 这套机制唯一的承诺是"页面上显示的那串码，日志里一定搜得到"。所以断言分两处：
/// 码必须落到同一次请求的 Items（错误页从这里取），且必须出现在那条日志的正文里。
/// </remarks>
public class ExceptionTraceMiddlewareTests
{
    [Fact]
    public async Task Unhandled_exception_is_logged_with_a_code_and_rethrown()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/query";
        var logger = new CapturingLogger<ExceptionTraceMiddleware>();
        var boom = new InvalidOperationException("写库失败");
        var middleware = new ExceptionTraceMiddleware(_ => throw boom, logger);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        // 中间件不能吞异常：吞掉之后外层的异常处理器渲染不出错误页，用户只会看到空白。
        Assert.Same(boom, thrown);

        var code = Assert.IsType<string>(context.Items[ExceptionTraceMiddleware.CodeItemKey]);
        Assert.True(ErrorTrace.IsWellFormed(code));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(boom, entry.Error);
        // 错误页显示的就是这串：日志里搜不到它，整套机制等于没做。
        Assert.Contains(code, entry.Message);
    }

    [Fact]
    public async Task Successful_request_records_nothing()
    {
        var context = new DefaultHttpContext();
        var logger = new CapturingLogger<ExceptionTraceMiddleware>();
        var middleware = new ExceptionTraceMiddleware(_ => Task.CompletedTask, logger);

        await middleware.InvokeAsync(context);

        Assert.Empty(logger.Entries);
        Assert.False(context.Items.ContainsKey(ExceptionTraceMiddleware.CodeItemKey));
    }

    /// <summary>
    /// 客户端断开不算故障。
    /// </summary>
    /// <remarks>
    /// 浏览器切页、关页签都会让请求带着取消异常结束。按错误记下来，除了把日志刷满，
    /// 还会给出一个现场根本看不到对应页面的回报码 —— 页面在码生成之前就没了。
    /// </remarks>
    [Fact]
    public async Task Client_disconnect_is_not_recorded_as_an_error()
    {
        var context = new DefaultHttpContext();
        var logger = new CapturingLogger<ExceptionTraceMiddleware>();
        var middleware = new ExceptionTraceMiddleware(_ => throw new OperationCanceledException(), logger);

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));

        Assert.Empty(logger.Entries);
        Assert.False(context.Items.ContainsKey(ExceptionTraceMiddleware.CodeItemKey));
    }
}

/// <summary>把日志条目原样留下来的最小 ILogger：用来断言"日志里能搜到那串码"。</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));
}
