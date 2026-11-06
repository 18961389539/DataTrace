using System.Text.Json;
using DataTrace.Application.Configuration;
using Microsoft.Extensions.Logging;

namespace DataTrace.Mcp;

/// <summary>把每次 MCP 工具调用写入现有审计表；审计失败不影响工具结果。</summary>
public sealed class ToolAudit(IAuditLogger audit, McpCallContext call, ILogger<ToolAudit> logger)
{
    public async Task RecordAsync(
        string tool,
        string outcome,
        int rows,
        object? detail = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = JsonSerializer.Serialize(new
            {
                rows,
                elapsedMs = (long?)null,
                detail
            });
            if (summary.Length > 2000)
            {
                summary = summary[..2000];
            }
            await audit.WriteAsync(
                call.UserName,
                $"mcp.{tool}",
                "MCP",
                tool,
                null,
                summary,
                cancellationToken,
                outcome: outcome,
                source: "MCP",
                sourceIp: call.SourceIp,
                correlationId: call.CorrelationId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "写入 MCP 工具审计失败：{Tool} {Outcome}", tool, outcome);
        }
    }
}
