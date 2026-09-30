using DataTrace.Shared;

namespace DataTrace.Web.Services;

/// <summary>
/// 给未处理异常配一个现场能回报的短码。
/// </summary>
/// <remarks>
/// 现状是：页面上只有一块"系统发生错误"，日志里有完整堆栈，两边接不上头 ——
/// 现场打电话只能说"刚才报错了"，工程师只能按时间戳在日志里翻。
/// 这里把异常发生时生成的码同时写进日志和 <see cref="HttpContext.Items"/>，
/// 再由 /Error 页把它显示出来，于是"用户看到的那串"与"日志里那行"是同一个值。
///
/// 挂在异常处理器内层（见 Program.cs 的注册顺序说明），只负责配码与记录，不吞异常：
/// 记完必须原样抛出，交给外层的 UseExceptionHandler 渲染错误页。
/// </remarks>
public sealed class ExceptionTraceMiddleware
{
    /// <summary>回报码在 <see cref="HttpContext.Items"/> 里的键。/Error 页按它取值。</summary>
    public const string CodeItemKey = "DataTrace.ErrorCode";

    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionTraceMiddleware> _logger;

    public ExceptionTraceMiddleware(RequestDelegate next, ILogger<ExceptionTraceMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        // 客户端断开时抛出的取消异常不是故障：浏览器切了页或关了页签都会触发，
        // 按错误记下来只会把日志刷满，还会给出一个现场根本看不到对应页面（页面早关了）的回报码。
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var code = ErrorTrace.NewCode();
            context.Items[CodeItemKey] = code;
            _logger.LogError(
                ex,
                "未处理异常（回报码 {ErrorCode}）：{Method} {Path}",
                code,
                context.Request.Method,
                context.Request.Path);
            throw;
        }
    }
}

public static class ExceptionTraceMiddlewareExtensions
{
    /// <summary>启用未处理异常的回报码。必须注册在异常处理器之后，见类型注释。</summary>
    public static IApplicationBuilder UseExceptionTrace(this IApplicationBuilder app)
        => app.UseMiddleware<ExceptionTraceMiddleware>();
}
