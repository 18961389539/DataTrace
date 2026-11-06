using System.Security.Claims;
using System.Text.Json;
using DataTrace.Application.Identity;
using DataTrace.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace DataTrace.Tests;

public sealed class McpCallContextTests
{
    [Fact]
    public void ResolveRange_defaults_to_last_24_hours_and_rejects_oversize_or_inverted_ranges()
    {
        var context = CreateContext(new McpOptions { MaxRangeDays = 31 });

        var (from, to) = context.ResolveRange(null, null);
        Assert.Equal(TimeSpan.FromDays(1), to - from);

        Assert.Throws<McpException>(() => context.ResolveRange(
            DateTimeOffset.Now.AddDays(-40), DateTimeOffset.Now));
        Assert.Throws<McpException>(() => context.ResolveRange(
            DateTimeOffset.Now, DateTimeOffset.Now.AddDays(-1)));
    }

    [Fact]
    public void ClampTake_uses_fallback_and_caps_at_max_page_size()
    {
        var context = CreateContext(new McpOptions { MaxPageSize = 100 });
        Assert.Equal(50, context.ClampTake(0));
        Assert.Equal(100, context.ClampTake(1000));
        Assert.Equal(25, context.ClampTake(25));
    }

    [Fact]
    public void Writes_require_write_scope_and_server_switch()
    {
        var noScope = CreateContext(new McpOptions { AllowWrites = true }, McpScopes.Read);
        Assert.Throws<McpException>(noScope.RequireWritesEnabled);

        var disabled = CreateContext(new McpOptions { AllowWrites = false }, McpScopes.Read, McpScopes.Write);
        Assert.Throws<McpException>(disabled.RequireWritesEnabled);

        var enabled = CreateContext(new McpOptions { AllowWrites = true }, McpScopes.Read, McpScopes.Write);
        enabled.RequireWritesEnabled();
    }

    [Fact]
    public void User_and_scopes_come_from_the_authenticated_principal()
    {
        var context = CreateContext(new McpOptions(), McpScopes.Read);
        Assert.Equal("admin", context.UserName);
        Assert.True(context.HasScope(McpScopes.Read));
        Assert.False(context.HasScope(McpScopes.Write));
    }

    [Fact]
    public void Serializer_writes_unspecified_times_with_local_offset()
    {
        var value = new DateTime(2026, 11, 6, 11, 30, 0, DateTimeKind.Unspecified);
        var json = JsonSerializer.Serialize(new { time = value }, McpJson.Options);

        using var document = JsonDocument.Parse(json);
        var text = document.RootElement.GetProperty("time").GetString();
        var expected = DateTime.SpecifyKind(value, DateTimeKind.Local)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task Heavy_query_gate_rejects_when_capacity_wait_times_out()
    {
        using var gate = new McpHeavyQueryGate(Options.Create(new McpOptions
        {
            HeavyQueryConcurrency = 1,
            RequestTimeoutSeconds = 1
        }));
        using var first = await gate.AcquireAsync(CancellationToken.None);
        await Assert.ThrowsAsync<McpException>(() => gate.AcquireAsync(CancellationToken.None));
    }

    private static McpCallContext CreateContext(McpOptions options, params string[] scopes)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "admin") }
                .Concat(scopes.Select(s => new Claim(McpAuthentication.ScopeClaim, s))),
            McpAuthentication.Scheme);
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new HttpContextAccessor { HttpContext = http };
        return new McpCallContext(accessor, Options.Create(options));
    }
}
