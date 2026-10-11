using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using DataTrace.Application.Identity;
using DataTrace.Domain.Constants;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataTrace.Infrastructure.Identity;

/// <summary>Rechecks a Blazor circuit against current Identity records before each sensitive config write.</summary>
public sealed class ConfigurationAuthorizer : IConfigurationAuthorizer
{
    private readonly AuthenticationStateProvider? _authenticationStateProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentityOptions _identityOptions;
    private readonly ILogger<ConfigurationAuthorizer> _logger;

    public ConfigurationAuthorizer(
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> identityOptions,
        ILogger<ConfigurationAuthorizer> logger,
        AuthenticationStateProvider? authenticationStateProvider = null)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _scopeFactory = scopeFactory;
        _identityOptions = identityOptions.Value;
        _logger = logger;
    }

    public Task AuthorizeChangeAsync(string actor, CancellationToken cancellationToken = default)
        => AuthorizeRoleAsync(actor, [AppRoles.Administrator, AppRoles.Engineer], cancellationToken);

    public async Task AuthorizeRoleAsync(
        string actor,
        IReadOnlyCollection<string> allowedRoles,
        CancellationToken cancellationToken = default,
        bool allowMustChangePassword = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_authenticationStateProvider is null)
        {
            Reject(actor, "当前运行环境未提供认证会话，已拒绝敏感操作");
        }

        var principal = (await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        await ValidatePrincipalAsync(principal, actor, allowedRoles, cancellationToken, allowMustChangePassword).ConfigureAwait(false);
    }

    public async Task AuthorizeCurrentRoleAsync(
        IReadOnlyCollection<string> allowedRoles,
        CancellationToken cancellationToken = default,
        bool allowMustChangePassword = false)
    {
        if (_authenticationStateProvider is null)
        {
            Reject("", "当前运行环境未提供认证会话，已拒绝敏感操作");
        }

        var principal = (await _authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false)).User;
        var actor = principal.Identity?.Name ?? "";
        await ValidatePrincipalAsync(principal, actor, allowedRoles, cancellationToken, allowMustChangePassword).ConfigureAwait(false);
    }

    private async Task ValidatePrincipalAsync(
        ClaimsPrincipal principal,
        string actor,
        IReadOnlyCollection<string> allowedRoles,
        CancellationToken cancellationToken,
        bool allowMustChangePassword)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            Reject(actor, "当前会话已失效，请重新登录后再操作");
        }

        var id = principal.FindFirstValue(_identityOptions.ClaimsIdentity.UserIdClaimType);
        var name = principal.Identity.Name;
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(name)
            || !string.Equals(name, actor, StringComparison.OrdinalIgnoreCase))
        {
            Reject(actor, "当前会话身份与操作人不一致，已拒绝操作");
        }

        // A Blazor circuit can live for hours. Always query with a fresh DbContext so an earlier
        // tracked Identity entity cannot hide a deleted user or an updated security stamp.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id).ConfigureAwait(false);
        if (user is null || !string.Equals(user.UserName, actor, StringComparison.OrdinalIgnoreCase))
        {
            Reject(actor, "账号已删除或已失效，请重新登录后再操作");
        }

        var lockoutEnd = await users.GetLockoutEndDateAsync(user).ConfigureAwait(false);
        if ((!allowMustChangePassword && user.MustChangePassword)
            || (lockoutEnd is { } until && until > DateTimeOffset.UtcNow))
        {
            Reject(actor, "账号当前不可执行该操作，请重新登录或完成密码处理");
        }

        var stampClaim = principal.FindFirstValue(_identityOptions.ClaimsIdentity.SecurityStampClaimType);
        var currentStamp = await users.GetSecurityStampAsync(user).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(stampClaim)
            || !string.Equals(stampClaim, currentStamp, StringComparison.Ordinal))
        {
            Reject(actor, "账号凭据已变更，旧会话已拒绝操作，请重新登录");
        }

        var roles = await users.GetRolesAsync(user).ConfigureAwait(false);
        if (!roles.Any(role => allowedRoles.Contains(role, StringComparer.Ordinal)))
        {
            Reject(actor, "当前账号已无执行该操作的权限，操作已拒绝");
        }
    }

    [DoesNotReturn]
    private void Reject(string actor, string reason)
    {
        _logger.LogWarning("Sensitive action denied for {Actor}: {Reason}", actor, reason);
        throw new ConfigurationAuthorizationException(reason);
    }
}
