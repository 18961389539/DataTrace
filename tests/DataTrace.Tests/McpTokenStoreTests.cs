using DataTrace.Application.Configuration;
using DataTrace.Application.Identity;
using DataTrace.Domain.Constants;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

public sealed class McpTokenStoreTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    [Fact]
    public async Task Create_returns_plaintext_once_and_validate_binds_user_and_scopes()
    {
        await using var provider = await CreateProviderAsync();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpTokenStore>();

        var created = await store.CreateAsync("admin", "交接助手", [McpScopes.Read], 30, "admin");

        Assert.StartsWith("dtmcp_", created.Plaintext);
        Assert.Equal("admin", created.Summary.UserName);
        Assert.Contains(McpScopes.Read, created.Summary.Scopes);

        var principal = await store.ValidateAsync(created.Plaintext);
        Assert.NotNull(principal);
        Assert.Equal("admin", principal!.UserName);
        Assert.Contains(McpScopes.Read, principal.Scopes);

        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConfigDbContext>>()
            .CreateDbContextAsync();
        var stored = await db.McpAccessTokens.AsNoTracking().SingleAsync();
        Assert.NotEqual(created.Plaintext, stored.TokenHash);
        Assert.DoesNotContain("dtmcp_", stored.TokenHash, StringComparison.Ordinal);
        Assert.Equal(created.Summary.Prefix, stored.Prefix);
    }

    [Fact]
    public async Task Wrong_secret_and_revoked_token_are_rejected()
    {
        await using var provider = await CreateProviderAsync();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpTokenStore>();

        var created = await store.CreateAsync("admin", "测试", [McpScopes.Read], 30, "admin");
        var parts = created.Plaintext.Split('_');
        var wrong = $"dtmcp_{parts[1]}_{new string('x', 40)}";
        Assert.Null(await store.ValidateAsync(wrong));

        Assert.True(await store.RevokeAsync(created.Summary.Id, "admin"));
        Assert.Null(await store.ValidateAsync(created.Plaintext));

        var rows = await store.ListAsync();
        Assert.Single(rows);
        Assert.NotNull(rows[0].RevokedAt);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        await using var provider = await CreateProviderAsync();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpTokenStore>();
        var created = await store.CreateAsync("admin", "过期", [McpScopes.Read], 1, "admin");

        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var entity = await db.McpAccessTokens.SingleAsync();
            entity.ExpiresAt = DateTime.Now.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        Assert.Null(await store.ValidateAsync(created.Plaintext));
    }

    [Fact]
    public async Task Write_scope_also_grants_read_and_actions_are_audited()
    {
        await using var provider = await CreateProviderAsync();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpTokenStore>();

        var created = await store.CreateAsync("admin", "写入令牌", [McpScopes.Write], 30, "admin");
        Assert.Contains(McpScopes.Read, created.Summary.Scopes);
        Assert.Contains(McpScopes.Write, created.Summary.Scopes);

        await store.RevokeAsync(created.Summary.Id, "admin");

        await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConfigDbContext>>()
            .CreateDbContextAsync();
        var actions = await db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "McpAccessToken")
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Contains("mcp.token.create", actions);
        Assert.Contains("mcp.token.revoke", actions);
    }

    [Fact]
    public async Task Create_rejects_unknown_scopes_and_missing_user()
    {
        await using var provider = await CreateProviderAsync();
        using var scope = provider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMcpTokenStore>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync("admin", "坏 scope", ["admin"], 30, "admin"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CreateAsync("nobody", "不存在", [McpScopes.Read], 30, "admin"));
    }

    private async Task<ServiceProvider> CreateProviderAsync()
    {
        var databasePath = _workspace.Path("config.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ConfigDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddDbContextFactory<ConfigDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddDataTraceIdentity();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<IMcpTokenStore, McpTokenStore>();

        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        await db.Database.EnsureCreatedAsync();

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roles.RoleExistsAsync(AppRoles.Administrator))
        {
            await roles.CreateAsync(new IdentityRole(AppRoles.Administrator));
        }
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = "admin", DisplayName = "管理员" };
        var result = await users.CreateAsync(user, "Admin@123");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, AppRoles.Administrator);
        return provider;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _workspace.Dispose();
    }
}
