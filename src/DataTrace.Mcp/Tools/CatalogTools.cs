using System.ComponentModel;
using DataTrace.Application.Configuration;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class CatalogTools(IConfigRepository config, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "list_stations", ReadOnly = true)]
    [Description("列出工站：主键、编码、名称、顺序、启用状态、首末站与点位数/曲线数。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<IReadOnlyList<StationDto>> ListStations(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var snapshot = await config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var items = snapshot.Stations
            .OrderBy(s => s.Sequence)
            .Select(s => new StationDto(
                s.Id, s.Code, s.Name, s.Sequence, s.Enabled, s.IsFirstStation, s.IsLastStation,
                s.Tags.Count, s.Curves.Count))
            .ToList();
        await audit.RecordAsync("list_stations", "Success", items.Count, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return items;
    }

    [McpServerTool(Name = "list_tags", ReadOnly = true)]
    [Description("列出指定工站的点位：主键、名称、单位、用途、规格/预警限值。工站不存在时返回错误。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<IReadOnlyList<TagDto>> ListTags(
        [Description("工站主键，可从 list_stations 获取")] int stationId,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var snapshot = await config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var station = snapshot.Stations.FirstOrDefault(s => s.Id == stationId)
            ?? throw new McpException($"工站 {stationId} 不存在。");
        var items = station.Tags
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new TagDto(
                t.Id, t.StationId, t.Name, t.Unit, t.Purpose.ToString(),
                t.LowerLimit, t.UpperLimit, t.WarningLowerLimit, t.WarningUpperLimit, t.TargetValue,
                t.IsRequired, t.Enabled, t.Source.ToString()))
            .ToList();
        await audit.RecordAsync("list_tags", "Success", items.Count, stationId, cancellationToken)
            .ConfigureAwait(false);
        return items;
    }

    [McpServerTool(Name = "list_work_orders", ReadOnly = true)]
    [Description("列出生产工单：工单号、型号、计划数量、状态、是否为当前工单与起止日期。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<IReadOnlyList<WorkOrderDto>> ListWorkOrders(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var orders = await config.GetWorkOrdersAsync(cancellationToken).ConfigureAwait(false);
        var items = orders
            .OrderByDescending(o => o.IsCurrent)
            .ThenByDescending(o => o.CreatedAt)
            .Select(o => new WorkOrderDto(
                o.Id, o.Code, o.Model, o.PlannedQuantity, o.Status.ToString(), o.IsCurrent,
                o.CurrentSince, o.StartDate, o.EndDate, o.Remark))
            .ToList();
        await audit.RecordAsync("list_work_orders", "Success", items.Count, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return items;
    }

    [McpServerTool(Name = "get_operational_config", ReadOnly = true)]
    [Description("读取运行配置摘要：当前型号、采集/MES/仿真开关、班次设置、型号清单与 PLC 名称。不返回 PLC 地址、连接凭据或内部路径。需要工程师或管理员。")]
    [Authorize(Policy = McpPolicies.Engineer)]
    public async Task<OperationalConfigDto> GetOperationalConfig(CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var snapshot = await config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var settings = snapshot.Settings;
        var result = new OperationalConfigDto(
            snapshot.ActiveRecipe?.Code ?? "",
            snapshot.ActiveRecipe?.Name,
            settings.CollectEnabled,
            settings.MesEnabled,
            settings.SimulatorAutoRun,
            settings.ShiftStartHour,
            settings.ShiftLengthHours,
            settings.RetentionYears,
            snapshot.Recipes.Select(r => new RecipeDto(
                r.Id, r.Code, r.Name, r.Model, r.Enabled, r.Limits.Count)).ToList(),
            snapshot.PlcConnections.Select(p => new PlcSummaryDto(p.Id, p.Name)).ToList());

        await audit.RecordAsync("get_operational_config", "Success", result.Recipes.Count,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result;
    }
}
