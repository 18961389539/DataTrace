using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DataTrace.Application.Identity;
using DataTrace.Infrastructure.Identity;
using DataTrace.Mcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace DataTrace.Web.Security;

public sealed class McpTokenAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// 只接受 Authorization: Bearer dtmcp_... 的 MCP 认证。刻意不读取 Identity Cookie：
/// 浏览器携带的会话凭据不能用来调用工具，避免 CSRF/DNS rebinding 绕过。
/// </summary>
public sealed class McpTokenAuthenticationHandler(
    IOptionsMonitor<McpTokenAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IMcpTokenStore tokens,
    UserManager<ApplicationUser> users)
    : AuthenticationHandler<McpTokenAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var plaintext = header["Bearer ".Length..].Trim();
        var token = await tokens.ValidateAsync(plaintext, Context.RequestAborted).ConfigureAwait(false);
        if (token is null)
        {
            return AuthenticateResult.Fail("invalid_token");
        }

        var user = await users.FindByNameAsync(token.UserName).ConfigureAwait(false);
        if (user is null || await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return AuthenticateResult.Fail("user_unavailable");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.UserName ?? token.UserName),
            new(McpAuthentication.TokenIdClaim, token.TokenId.ToString(CultureInfo.InvariantCulture))
        };
        foreach (var scope in token.Scopes)
        {
            claims.Add(new Claim(McpAuthentication.ScopeClaim, scope));
        }
        foreach (var role in await users.GetRolesAsync(user).ConfigureAwait(false))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
