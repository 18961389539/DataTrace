namespace DataTrace.Web.Services;

/// <summary>
/// 不带凭据的表单 POST（登录）既不能靠 SameSite 拦跨站提交，也用不上防伪令牌：
/// 登录页走交互式渲染，<c>AntiforgeryToken</c> 只在静态 SSR 才拿得到 HttpContext 并输出令牌
/// （见 Login.razor 的说明）。于是退回到浏览器一定会带、页面脚本改不了的
/// Origin / Referer 上判断来源。
/// </summary>
/// <remarks>
/// 只比对方（host + 端口），不比路径：登录表单从任何本站页面提交都该放行。
/// 反向代理改写 host 的部署需要另行处理 X-Forwarded-*，本产品是 Kestrel 直连。
/// </remarks>
public static class CrossSiteRequestGuard
{
    /// <summary>
    /// 请求来源与本站是否一致。Origin 优先，缺失时看 Referer。
    /// 两者都缺失时放行：浏览器发起的跨站表单提交必然带 Origin，
    /// 都没有说明不是浏览器表单（例如运维用的脚本），硬拦只会误伤。
    /// </summary>
    public static bool IsSameSite(string? origin, string? referer, string requestHost, int requestPort)
    {
        if (Matches(origin, requestHost, requestPort) || Matches(referer, requestHost, requestPort))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(origin) && string.IsNullOrWhiteSpace(referer);
    }

    private static bool Matches(string? url, string requestHost, int requestPort)
        => !string.IsNullOrWhiteSpace(url)
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Port == requestPort
           && string.Equals(uri.Host, requestHost, StringComparison.OrdinalIgnoreCase);
}
