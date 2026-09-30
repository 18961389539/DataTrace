using Microsoft.AspNetCore.Identity;

namespace DataTrace.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 带的是"初始口令还没换掉"。只有生产首启动随机生成的引导管理员会置位；
    /// 登录后被管道拦到改密页，改完由 <c>UserAdministration.ChangePasswordAsync</c> 清掉。
    /// </summary>
    /// <remarks>
    /// 以前生产也会种入 <c>Admin@123</c> 这类写死在源码里的口令，等于每套现场都有一把
    /// 公开钥匙。现在改为随机生成 + 首登强制改密，这个标记就是强制那一步的依据。
    /// </remarks>
    public bool MustChangePassword { get; set; }
}
