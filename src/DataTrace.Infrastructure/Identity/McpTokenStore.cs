using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataTrace.Application.Configuration;
using DataTrace.Application.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Identity;

public sealed class McpTokenStore(
    IDbContextFactory<ConfigDbContext> dbFactory,
    UserManager<ApplicationUser> users,
    IAuditLogger audit,
    ILogger<McpTokenStore> logger) : IMcpTokenStore
{
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(5);
    private const string TokenPrefix = "dtmcp_";

    public async Task<IReadOnlyList<McpTokenSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.McpAccessTokens.AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToSummary).ToList();
    }

    public async Task<CreatedMcpToken> CreateAsync(
        string userName,
        string name,
        IReadOnlyList<string> scopes,
        int lifetimeDays,
        string actor,
        CancellationToken cancellationToken = default)
    {
        userName = (userName ?? "").Trim();
        name = (name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("令牌用途不能为空。");
        }
        if (name.Length > 100)
        {
            throw new InvalidOperationException("令牌用途不能超过 100 个字符。");
        }
        if (lifetimeDays is < 1 or > 365)
        {
            throw new InvalidOperationException("有效期必须在 1–365 天之间。");
        }
        if (scopes.Count == 0 || scopes.Any(s => !McpScopes.IsKnown(s)))
        {
            throw new InvalidOperationException("scope 只能包含 read 或 write。");
        }

        var user = await users.FindByNameAsync(userName).ConfigureAwait(false)
            ?? throw new InvalidOperationException("绑定用户不存在。");
        if (await users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            throw new InvalidOperationException("绑定用户已被锁定，不能创建令牌。");
        }

        var secret = CreateSecret();
        var now = DateTime.Now;
        var effectiveScopes = scopes.Contains(McpScopes.Write, StringComparer.OrdinalIgnoreCase)
            ? McpScopes.All
            : scopes;
        var entity = new McpAccessToken
        {
            UserName = user.UserName ?? userName,
            Name = name,
            Prefix = secret[..8],
            TokenHash = Hash(secret),
            Scopes = string.Join(',', effectiveScopes.Distinct(StringComparer.OrdinalIgnoreCase)),
            CreatedAt = now,
            CreatedBy = actor,
            ExpiresAt = now.AddDays(lifetimeDays)
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.McpAccessTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var summary = ToSummary(entity);
        await WriteAuditAsync(
            actor,
            "mcp.token.create",
            entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            JsonSerializer.Serialize(new { entity.UserName, entity.Name, entity.Scopes, entity.ExpiresAt }),
            cancellationToken).ConfigureAwait(false);

        return new CreatedMcpToken(summary, $"{TokenPrefix}{entity.Id}_{secret}");
    }

    public async Task<bool> RevokeAsync(int id, string actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.McpAccessTokens.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }
        if (entity.RevokedAt is null)
        {
            entity.RevokedAt = DateTime.Now;
            entity.RevokedBy = actor;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(
                actor,
                "mcp.token.revoke",
                entity.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(new { entity.UserName, entity.Name }),
                null,
                cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<McpTokenPrincipal?> ValidateAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        if (!TryParse(plaintext, out var id, out var secret))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.McpAccessTokens.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null || entity.RevokedAt is not null || entity.ExpiresAt <= DateTime.Now)
        {
            return null;
        }

        var expected = Convert.FromBase64String(entity.TokenHash);
        var actual = Convert.FromBase64String(Hash(secret));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return null;
        }

        await TouchLastUsedAsync(id, cancellationToken).ConfigureAwait(false);
        return new McpTokenPrincipal(
            entity.Id,
            entity.UserName,
            entity.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task TouchLastUsedAsync(int id, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var entity = await db.McpAccessTokens.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                return;
            }
            var now = DateTime.Now;
            if (entity.LastUsedAt is { } last && now - last < LastUsedWriteInterval)
            {
                return;
            }
            entity.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 最近使用时间只是运维信息，写失败不能影响鉴权。
            logger.LogDebug(ex, "更新 MCP 令牌最近使用时间失败：{TokenId}", id);
        }
    }

    private async Task WriteAuditAsync(
        string actor,
        string action,
        string entityKey,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken)
    {
        try
        {
            await audit.WriteAsync(
                actor,
                action,
                "McpAccessToken",
                entityKey,
                oldValue,
                newValue,
                cancellationToken,
                source: "MCP Token 管理").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "写入 MCP 令牌审计失败：{Action} {EntityKey}", action, entityKey);
        }
    }

    private static bool TryParse(string plaintext, out int id, out string secret)
    {
        id = 0;
        secret = "";
        if (string.IsNullOrWhiteSpace(plaintext) || !plaintext.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }
        var rest = plaintext[TokenPrefix.Length..];
        var separator = rest.IndexOf('_');
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }
        if (!int.TryParse(rest[..separator], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out id))
        {
            return false;
        }
        secret = rest[(separator + 1)..];
        return secret.Length is >= 20 and <= 128;
    }

    private static string CreateSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Hash(string secret)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static McpTokenSummary ToSummary(McpAccessToken entity)
        => new(
            entity.Id,
            entity.Name,
            entity.UserName,
            entity.Prefix,
            entity.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            entity.CreatedAt,
            entity.ExpiresAt,
            entity.RevokedAt,
            entity.LastUsedAt);
}
