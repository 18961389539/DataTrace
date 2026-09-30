using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DataTrace.Infrastructure.Identity;

/// <summary>
/// 把"必须改密"写进登录主体，识别与拦截分居两处：这里出标记，Web 管道读标记。
/// </summary>
/// <remarks>
/// 也可以在每个认证请求上查一次库，但那会给所有请求都加一次数据库往返；
/// 标记跟着 Cookie / 电路走，零成本。代价是改完密码要让主体刷新一次
/// （<see cref="SignInManager{TUser}.RefreshSignInAsync"/>），否则旧标记还在，
/// 用户会被自己刚改完的密码挡在改密页上。
/// </remarks>
public sealed class ApplicationUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    /// <summary>主体上的标记名。Web 管道与登录流程共用它，避免两边拼错字符串。</summary>
    public const string MustChangePasswordClaim = "datatrace:must_change_password";

    public ApplicationUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user).ConfigureAwait(false);
        if (user.MustChangePassword)
        {
            identity.AddClaim(new Claim(MustChangePasswordClaim, "1"));
        }

        return identity;
    }
}
