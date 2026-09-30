namespace DataTrace.Shared;

/// <summary>
/// 未处理异常的「回报码」：现场只看得到一块错误页，工程师只看得到日志，两边得能对上号。
/// </summary>
/// <remarks>
/// 码由 Guid 生成，8 位大写十六进制。
/// 不用 <c>HttpContext.TraceIdentifier</c>：它形如 <c>0HN7B2K9Q3V1A:00000001</c>，
/// 尾部是每个连接各自的请求计数 —— 截短之后不同连接会撞在一起，整串又长到没法在电话里念。
/// </remarks>
public static class ErrorTrace
{
    /// <summary>回报码长度。8 位十六进制 = 32 位随机量，单机一天几十条的量级撞不上。</summary>
    public const int CodeLength = 8;

    /// <summary>新生成一个回报码。只有这里会调随机数，格式本身可单独测。</summary>
    public static string NewCode() => FromGuid(Guid.NewGuid());

    /// <summary>从 Guid 取码。抽出来是为了让格式可断言，不必去碰随机源。</summary>
    public static string FromGuid(Guid value) => value.ToString("N")[..CodeLength].ToUpperInvariant();

    /// <summary>
    /// 判断一个字符串是不是本系统生成的码。
    /// </summary>
    /// <remarks>
    /// 只认"固定长度的大写十六进制"。错误页是匿名可达的，而这个值会被原样渲染出来，
    /// 不做形状校验就等于给了一个反射任意文本的出口。
    /// </remarks>
    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != CodeLength)
        {
            return false;
        }

        foreach (var ch in code)
        {
            var isHex = (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
