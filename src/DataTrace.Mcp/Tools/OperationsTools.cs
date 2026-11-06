using System.ComponentModel;
using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class OperationsTools(
    IConfigRepository config,
    ICollectorDiagnostics diagnostics,
    McpCallContext call,
    ToolAudit audit)
{
    [McpServerTool(Name = "get_mes_outbox_status", ReadOnly = true)]
    [Description("读取 MES 出站队列状态：待推送条数、最老积压时间、最近一次尝试与成功时间、最近错误。需要工程师或管理员。")]
    [Authorize(Policy = McpPolicies.Engineer)]
    public async Task<MesOutboxStatusDto> GetMesOutboxStatus(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var status = await config.GetMesOutboxStatusAsync(cancellationToken).ConfigureAwait(false);
        var result = new MesOutboxStatusDto(
            status.PendingCount,
            status.OldestPendingAt,
            status.LastAttemptAt,
            status.LastAttemptSucceeded,
            status.LastError,
            status.LastSuccessAt);
        await audit.RecordAsync("get_mes_outbox_status", "Success", status.PendingCount,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }

    [McpServerTool(Name = "get_collection_diagnostics", ReadOnly = true)]
    [Description("读取采集诊断快照：循环耗时与超时次数、站级 P95/P99、在途与超龄任务、PLC 失败摘要。数据来自内存，不查历史库。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<CollectionDiagnosticsDto> GetCollectionDiagnostics(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var loop = diagnostics.Loop;
        var result = new CollectionDiagnosticsDto(
            diagnostics.CollectionMaxAgeSeconds,
            new CollectorLoopDto(
                loop.Capacity,
                loop.AverageWorkMs,
                loop.MaxWorkMs,
                loop.OverrunCount,
                loop.Ticks.TakeLast(10).Select(t => new CollectorLoopTickDto(
                    t.At, t.ConfiguredIntervalMs, t.WorkMs, t.ScanMs, t.HeartbeatMs,
                    t.PlcCount, t.TriggeredCount, t.Overran)).ToList()),
            diagnostics.RecentStationLatencies.Select(l => new StationLatencyDto(
                l.StationCode, l.SampleCount, l.P95Ms, l.P99Ms, l.LastSampleAt)).ToList(),
            diagnostics.ActiveCollections.Select(ToDto).ToList(),
            diagnostics.OverdueCollections.Select(ToDto).ToList(),
            diagnostics.PlcTraffic.Select(t => new PlcTrafficSummaryDto(
                t.PlcConnectionId, t.Name, t.TotalCount, t.FailureCount,
                t.LastDurationMs, t.MaxDurationMs, t.LastSuccessAt,
                t.RecentFailures.Take(3).Select(f => new PlcFailureDto(
                    f.At, f.Address, f.DurationMs, f.Error)).ToList())).ToList());

        await audit.RecordAsync("get_collection_diagnostics", "Success", result.Latencies.Count,
            new { activeCollections = result.ActiveCollections.Count, overdueCollections = result.OverdueCollections.Count },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static ActiveCollectionDto ToDto(ActiveCollectionView view)
        => new(view.StationCode, view.StartedAt, view.ElapsedMs);
}
