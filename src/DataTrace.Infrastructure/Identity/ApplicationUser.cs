using Microsoft.AspNetCore.Identity;

namespace DataTrace.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}
