using Microsoft.AspNetCore.Identity;

namespace DataTrace.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 为兼容旧配置库保留。当前版本不再要求首次登录改密，并会在启动时清除此标记。
    /// </summary>
    public bool MustChangePassword { get; set; }
}
