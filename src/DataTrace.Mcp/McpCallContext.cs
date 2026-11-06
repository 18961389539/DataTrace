using System.Security.Claims;
using DataTrace.Application.Identity;
using DataTrace.Application.Quality;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace DataTrace.Mcp;

/// <summary>一次 MCP 工具调用的身份、来源与限制口径。工具不直接使用 HttpContext。</summary>
public sealed class McpCallContext(IHttpContextAccessor http, IOptions<McpOptions> options)
{
    private readonly McpOptions _options = options.Value;

    public string UserName => http.HttpContext?.User.Identity?.Name ?? "mcp";

    public string? SourceIp => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string CorrelationId => http.HttpContext?.TraceIdentifier ?? "";

    public IReadOnlyList<string> Scopes
        => http.HttpContext?.User.FindAll(McpAuthentication.ScopeClaim)
            .Select(c => c.Value)
            .ToArray() ?? [];

    public IReadOnlyCollection<string> Roles
        => http.HttpContext?.User.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToArray() ?? [];

    public QualityIncidentActor Actor => new(UserName, Roles);

    public bool HasScope(string scope)
        => Scopes.Contains(scope, StringComparer.OrdinalIgnoreCase);

    public void RequireRead()
    {
        if (!HasScope(McpScopes.Read))
        {
            throw new McpException("当前令牌没有 read scope。");
        }
    }

    public void RequireWritesEnabled()
    {
        RequireRead();
        if (!HasScope(McpScopes.Write))
        {
            throw new McpException("当前令牌没有 write scope。");
        }
        if (!_options.AllowWrites)
        {
            throw new McpException("服务端未开启 MCP 写入功能（Mcp:AllowWrites=false）。");
        }
    }

    /// <summary>解析工具时间范围：缺省为最近 24 小时；超过配置上限直接拒绝，避免慢查询拖住采集。</summary>
    public (DateTime From, DateTime To) ResolveRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        var toLocal = (to?.ToLocalTime().DateTime) ?? DateTime.Now;
        var fromLocal = (from?.ToLocalTime().DateTime) ?? toLocal.AddDays(-1);
        if (fromLocal > toLocal)
        {
            throw new McpException("from 必须早于或等于 to。");
        }
        if ((toLocal - fromLocal).TotalDays > _options.MaxRangeDays)
        {
            throw new McpException($"时间范围最多 {_options.MaxRangeDays} 天。");
        }
        return (fromLocal, toLocal);
    }

    public int ClampTake(int take, int fallback = 50)
    {
        if (take <= 0)
        {
            take = fallback;
        }
        return Math.Clamp(take, 1, _options.MaxPageSize);
    }

    public int MaxTrendPoints => _options.MaxTrendPoints;

    public int MaxSpcSamples => _options.MaxSpcSamples;
}
