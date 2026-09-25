namespace DataTrace.Web.Services;

/// <summary>
/// 登录后回跳目标（<c>ReturnUrl</c>）的安全阀：只认站内根相对路径。
/// </summary>
public static class ReturnUrl
{
    /// <summary>查询串与表单里都用这个名字，避免 Cookie 认证发 ReturnUrl、组件发 returnUrl 两套拼写。</summary>
    public const string QueryKey = "ReturnUrl";

    /// <summary>用户名或密码不对。登录页上的 error 参数沿用 "1"，旧书签不会因为改常量而失配。</summary>
    public const string BadCredentialsError = "1";

    /// <summary>账号被 Identity 临时锁定（连续失败次数超限）。</summary>
    public const string LockedError = "locked";

    /// <summary>提交来源不是本站，按跨站表单拒绝。</summary>
    public const string CrossSiteError = "crosssite";

    /// <summary>
    /// 能不能直接跳。只放行单个斜杠开头的路径：<c>//host/path</c> 和 <c>/\host</c>
    /// 都会被浏览器解释成另一个站点，那就是开放重定向。
    /// </summary>
    public static bool IsLocal(string? url)
        => !string.IsNullOrEmpty(url)
           && url[0] == '/'
           && !url.StartsWith("//", StringComparison.Ordinal)
           && !url.StartsWith("/\\", StringComparison.Ordinal);

    /// <summary>登录成功后的去处：目标合法就回目标，否则回看板。</summary>
    public static string AfterSignIn(string? url)
        => IsLocal(url) ? url! : "/";

    /// <summary>
    /// 登录没成时的去处。必须把目标一起带回去，否则用户重输一次就丢掉深链接，
    /// 从 MES 拷进来的那条记录地址得从头再点一遍。
    /// </summary>
    /// <param name="error">错误码：登录页据此给出不同说法（口令错 / 被锁定 / 来源不对）。
    /// 一律笼统说"用户名或密码错误"会让被锁定的操作员反复重试，把锁定时间越推越长。</param>
    public static string AfterSignInFailed(string? url, string error = BadCredentialsError)
        => IsLocal(url)
            ? $"/login?error={error}&{QueryKey}={Uri.EscapeDataString(url!)}"
            : $"/login?error={error}";
}
