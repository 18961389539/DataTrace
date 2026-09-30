namespace DataTrace.Infrastructure.Seeding;

/// <summary>
/// 种子账号策略。默认值面向开发与演示。
/// </summary>
/// <remarks>
/// <b>生产必须用 <see cref="Production"/>。</b> 默认策略会把 <c>admin/Admin@123</c> 这类
/// 写在源码里的口令种进库里 —— 那等于每套现场都配了一把公开钥匙，
/// 连"只能局域网访问"都挡不住：源码是公开的，口令就是公开的。
/// </remarks>
public sealed class SeedUserOptions
{
    /// <summary>
    /// true = 种入演示账号 <c>admin / engineer / operator / viewer</c>，口令写在源码里。
    /// 仅限开发、演示与自动化测试。
    /// </summary>
    public bool DemoUsers { get; init; } = true;

    /// <summary>
    /// 引导管理员（<c>admin</c>）的口令。留空则随机生成一次并打印到日志，推荐留空。
    /// 无论哪种来源，首次登录都会被要求改密。
    /// </summary>
    public string? AdminPassword { get; init; }

    /// <summary>生产策略：不写死任何口令，只引导出一个必须改密码的管理员。</summary>
    public static SeedUserOptions Production { get; } = new() { DemoUsers = false };
}
