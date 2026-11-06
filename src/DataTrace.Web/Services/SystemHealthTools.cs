using System.ComponentModel;
using DataTrace.Mcp;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Web.Services;

[McpServerToolType]
public sealed class SystemHealthTools(IHealthProbe health, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "system_health", ReadOnly = true)]
    [Description("读取应用健康报告：配置库、数据盘、磁盘空间、采集心跳、采集就绪与工站故障。只返回结论与摘要。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<HealthReport> SystemHealth(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var report = await health.CheckAsync(cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync("system_health", "Success", report.Checks.Count,
            new { report.Status }, cancellationToken).ConfigureAwait(false);
        return report;
    }
}
