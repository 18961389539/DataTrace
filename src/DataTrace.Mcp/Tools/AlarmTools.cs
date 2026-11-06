using System.ComponentModel;
using DataTrace.Application.Alarms;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class AlarmTools(IAlarmIncidents alarms, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "list_alarms", ReadOnly = true)]
    [Description("列出设备报警：待关注（未接手或未解除）与最近已关闭。take 同时限制两类条数，最大 50。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<IReadOnlyList<AlarmDto>> ListAlarms(
        [Description("最近已关闭报警的条数，默认 20，最大 50")] int take = 20,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var recent = Math.Clamp(take, 1, 50);
        var attention = await alarms.ListAttentionAsync(cancellationToken).ConfigureAwait(false);
        var closed = await alarms.ListRecentClosedAsync(recent, cancellationToken).ConfigureAwait(false);
        var items = attention.Concat(closed).Select(ToDto).ToList();
        await audit.RecordAsync("list_alarms", "Success", items.Count, new { take = recent },
            cancellationToken).ConfigureAwait(false);
        return items;
    }

    internal static AlarmDto ToDto(AlarmIncidentDraft alarm)
        => new(
            alarm.Id,
            alarm.Key,
            alarm.Kind.ToString(),
            alarm.Message,
            alarm.RaisedAt,
            alarm.AcknowledgedAt,
            alarm.AcknowledgedBy,
            alarm.ClearedAt,
            alarm.NeedsOwner,
            alarm.StillActive);
}
