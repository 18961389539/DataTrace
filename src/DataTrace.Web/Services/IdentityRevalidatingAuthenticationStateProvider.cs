using System.Security.Claims;
using DataTrace.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataTrace.Web.Services;

/// <summary>Periodically revalidates an open Blazor circuit against the current Identity user and roles.</summary>
public sealed class IdentityRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentityOptions _identityOptions;

    public IdentityRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> identityOptions)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _identityOptions = identityOptions.Value;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var userId = principal.FindFirstValue(_identityOptions.ClaimsIdentity.UserIdClaimType);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        // Never reuse a UserManager/DbContext held by this long-lived circuit for revalidation.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId).ConfigureAwait(false);
        if (user is null || user.MustChangePassword)
        {
            return false;
        }

        var lockoutEnd = await users.GetLockoutEndDateAsync(user).ConfigureAwait(false);
        if (lockoutEnd is { } until && until > DateTimeOffset.UtcNow)
        {
            return false;
        }

        var expectedStamp = principal.FindFirstValue(_identityOptions.ClaimsIdentity.SecurityStampClaimType);
        var currentStamp = await users.GetSecurityStampAsync(user).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(expectedStamp)
            || !string.Equals(expectedStamp, currentStamp, StringComparison.Ordinal))
        {
            return false;
        }

        // Role claims are part of the circuit principal. Refresh the circuit after either a grant or revocation
        // so route checks and AuthorizeView never continue to use an old role set.
        var storedRoles = await users.GetRolesAsync(user).ConfigureAwait(false);
        var principalRoles = principal.FindAll(_identityOptions.ClaimsIdentity.RoleClaimType)
            .Select(claim => claim.Value)
            .ToHashSet(StringComparer.Ordinal);
        return storedRoles.ToHashSet(StringComparer.Ordinal).SetEquals(principalRoles);
    }
}
