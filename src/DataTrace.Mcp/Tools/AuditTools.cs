using System.ComponentModel;
using DataTrace.Application.Configuration;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class AuditTools(IAuditLogger auditLog, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "get_audit_logs", ReadOnly = true)]
    [Description("查询审计日志：时间范围最多 7 天，take 最大 200。仅管理员可用。")]
    [Authorize(Policy = McpPolicies.Admin)]
    public async Task<AuditPageDto> GetAuditLogs(
        [Description("ISO 8601 起始时间，含时区偏移；缺省为 to 前 24 小时")] DateTimeOffset? from = null,
        [Description("ISO 8601 截止时间，含时区偏移；缺省为当前时间")] DateTimeOffset? to = null,
        [Description("按用户名过滤")] string? user = null,
        [Description("按动作码过滤，例如 mcp.query_records")] string? action = null,
        [Description("按来源过滤，例如 MCP、Blazor Server UI")] string? source = null,
        [Description("每页条数，默认 50，最大 200")] int take = 50,
        [Description("跳过的条数")] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ResolveRange(from, to);
        if ((toLocal - fromLocal).TotalDays > 7)
        {
            throw new ModelContextProtocol.McpException("审计日志查询范围最多 7 天。");
        }

        var pageSize = Math.Clamp(take, 1, 200);
        var (items, total) = await auditLog.QueryAsync(
            fromInclusive: fromLocal,
            toInclusive: toLocal,
            user: string.IsNullOrWhiteSpace(user) ? null : user.Trim(),
            action: string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
            source: string.IsNullOrWhiteSpace(source) ? null : source.Trim(),
            skip: Math.Max(0, skip),
            take: pageSize,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var result = new AuditPageDto(total, items.Select(x => new AuditLogDto(
            x.Id, x.Time, x.UserName, x.Action, x.EntityType, x.EntityKey,
            x.Outcome, x.Source, x.SourceIp, x.CorrelationId)).ToList());

        await audit.RecordAsync("get_audit_logs", "Success", result.Items.Count,
            new { fromLocal, toLocal, user, action, source, take = pageSize, skip },
            cancellationToken).ConfigureAwait(false);
        return result;
    }
}
