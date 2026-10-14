using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DataTrace.Infrastructure.Identity;

/// <summary>Creates the application user's standard Identity principal.</summary>
public sealed class ApplicationUserClaimsPrincipalFactory
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public ApplicationUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }
}
