using System.ComponentModel;
using DataTrace.Application.Realtime;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class StatusTools(IRuntimeStatusHub hub, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "get_line_status", ReadOnly = true)]
    [Description("获取产线实时状态：各工站状态、PLC 连接、当前型号、最近采集记录、采集就绪与连续 NG。数据来自内存快照，不查历史库。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<LineStatusDto> GetLineStatus(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var readiness = hub.CollectionReadiness;
        var result = new LineStatusDto(
            hub.ActiveRecipeCode,
            hub.ActiveRecipeName,
            hub.LastCollectorAt,
            readiness.Ready,
            readiness.Reason,
            hub.Stations.Select(s => new StationStatusDto(
                s.StationId,
                s.StationCode,
                s.StationName,
                s.Sequence,
                s.State.ToString(),
                s.LastPalletCode,
                s.LastSerialNo,
                s.LastResultCode,
                s.LastJudgement.ToString(),
                s.LastCompleteTime,
                s.LastPieceGap?.TotalSeconds,
                s.LastDurationMs,
                s.LastError,
                s.LastWriteBackOk,
                s.LastWriteBackAttempts,
                s.LastTags.Select(t => new LiveTagDto(
                    t.Name, t.Display, t.Unit, t.NumericValue, t.TextValue, t.OutOfLimit, t.Warning)).ToList(),
                s.LastCurves.Select(c => new LiveCurveDto(
                    c.Name, c.Judgement?.ToString(), c.JudgementReason)).ToList())).ToList(),
            hub.Plcs.Select(p => new PlcStatusDto(p.PlcConnectionId, p.Name, p.Connected, p.LastError)).ToList(),
            hub.Recent.Select(r => new CollectFeedItemDto(
                r.MonthKey, r.RecordId, r.StationCode, r.PalletCode, r.SerialNo,
                r.RecipeCode, r.ResultCode, r.Judgement.ToString(), r.CompleteTime,
                r.DurationMs, r.Error)).ToList(),
            hub.NgStreaks.Select(n => new NgStreakDto(
                n.StationId, n.StationCode, n.Count, n.Kind.ToString())).ToList());

        await audit.RecordAsync("get_line_status", "Success", result.Stations.Count, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result;
    }
}
