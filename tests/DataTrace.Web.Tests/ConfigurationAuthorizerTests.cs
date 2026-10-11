using System.Security.Claims;
using DataTrace.Application.Configuration;
using DataTrace.Application.Identity;
using DataTrace.Domain.Constants;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace DataTrace.Web.Tests;

public sealed class ConfigurationAuthorizerTests
{
    [Fact]
    public async Task UpdatedSecurityStampInAnotherScopeRejectsExistingCircuit()
    {
        await using var setup = await CreateAsync();

        await using (var scope = setup.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByNameAsync("admin");
            Assert.NotNull(user);
            Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        }

        await Assert.ThrowsAsync<ConfigurationAuthorizationException>(
            () => setup.Authorizer.AuthorizeChangeAsync("admin"));
    }

    [Fact]
    public async Task DeletedAccountInAnotherScopeRejectsExistingCircuit()
    {
        await using var setup = await CreateAsync();

        await using (var scope = setup.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByNameAsync("admin");
            Assert.NotNull(user);
            Assert.True((await users.DeleteAsync(user)).Succeeded);
        }

        await Assert.ThrowsAsync<ConfigurationAuthorizationException>(
            () => setup.Authorizer.AuthorizeChangeAsync("admin"));
    }

    [Fact]
    public async Task MissingAuthenticationStateCanResolveButSensitiveActionsFailClosed()
    {
        await using var setup = await CreateAsync();
        await using var scope = setup.Services.CreateAsyncScope();
        var authorizer = new ConfigurationAuthorizer(
            setup.Services.GetRequiredService<IServiceScopeFactory>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(),
            scope.ServiceProvider.GetRequiredService<ILogger<ConfigurationAuthorizer>>());

        await Assert.ThrowsAsync<ConfigurationAuthorizationException>(
            () => authorizer.AuthorizeChangeAsync("admin"));
    }

    [Fact]
    public async Task EngineerCanListAssignableUsersThroughCurrentIdentity()
    {
        await using var setup = await CreateAsync("engineer", AppRoles.Engineer);
        await using var scope = setup.Services.CreateAsyncScope();
        var administration = CreateUserAdministration(scope, setup.Authorizer);

        var assignable = await administration.ListAssignableAsync("engineer");

        Assert.Contains(assignable, account => account.UserName == "engineer" && account.Roles.Contains(AppRoles.Engineer));
    }

    [Fact]
    public async Task OperatorCannotListAssignableUsersForManagementAction()
    {
        await using var setup = await CreateAsync("operator", AppRoles.Operator);
        await using var scope = setup.Services.CreateAsyncScope();
        var administration = CreateUserAdministration(scope, setup.Authorizer);

        await Assert.ThrowsAsync<ConfigurationAuthorizationException>(
            () => administration.ListAssignableAsync("operator"));
    }

    private static UserAdministration CreateUserAdministration(
        AsyncServiceScope scope,
        ConfigurationAuthorizer authorizer)
    {
        var services = scope.ServiceProvider;
        return new UserAdministration(
            services.GetRequiredService<UserManager<ApplicationUser>>(),
            Mock.Of<IAuditLogger>(),
            services.GetRequiredService<ConfigDbContext>(),
            authorizer);
    }

    private static async Task<TestSetup> CreateAsync(string actorName = "admin", string actorRole = AppRoles.Administrator)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ConfigDbContext>(options => options.UseSqlite(connection));
        services.AddDataTraceIdentity();
        var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            await db.Database.EnsureCreatedAsync();

            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            foreach (var roleName in AppRoles.All)
            {
                Assert.True((await roles.CreateAsync(new IdentityRole(roleName))).Succeeded);
            }
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = actorName,
                DisplayName = actorName,
                Email = $"{actorName}@datatrace.local"
            };
            Assert.True((await users.CreateAsync(user, "Start@123")).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, actorRole)).Succeeded);

            var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
            var principal = await factory.CreateAsync(user);
            var authState = new StaticAuthenticationStateProvider(principal);
            var authorizer = new ConfigurationAuthorizer(
                provider.GetRequiredService<IServiceScopeFactory>(),
                scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>(),
                scope.ServiceProvider.GetRequiredService<ILogger<ConfigurationAuthorizer>>(),
                authState);
            await authorizer.AuthorizeRoleAsync(actorName, [actorRole]);
            return new TestSetup(connection, provider, authorizer);
        }
    }

    private sealed class StaticAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class TestSetup(SqliteConnection connection, ServiceProvider services, ConfigurationAuthorizer authorizer)
        : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public ConfigurationAuthorizer Authorizer { get; } = authorizer;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
