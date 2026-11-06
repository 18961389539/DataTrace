using System.ComponentModel;
using DataTrace.Application.Quality;
using DataTrace.Domain.Enums;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class QualityTools(IQualityIncidentService incidents, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "list_quality_incidents", ReadOnly = true)]
    [Description("筛选质量异常单：状态、工站、负责人、关键词。take 最大 50。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<QualityIncidentPageDto> ListQualityIncidents(
        [Description("状态：Open / InProgress / PendingReview / Closed")] string? status = null,
        [Description("工站主键")] int? stationId = null,
        [Description("负责人用户名")] string? owner = null,
        [Description("在单号、标题、描述中搜索")] string? keyword = null,
        [Description("每页条数，默认 25，最大 50")] int take = 25,
        [Description("跳过的条数")] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var result = await incidents.ListAsync(new QualityIncidentQuery
        {
            Status = ParseStatus(status),
            StationId = stationId,
            Owner = string.IsNullOrWhiteSpace(owner) ? null : owner.Trim(),
            Keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword.Trim(),
            Skip = Math.Max(0, skip),
            Take = Math.Clamp(take, 1, 50)
        }, cancellationToken).ConfigureAwait(false);

        var items = result.Items.Select(x => new QualityIncidentSummaryDto(
            x.Id, x.IncidentNumber, x.Title, x.StationCode, x.RecipeCode,
            x.Severity.ToString(), x.Status.ToString(), x.Owner,
            x.CreatedAt, x.UpdatedAt, x.Version, x.EvidenceCount)).ToList();

        await audit.RecordAsync("list_quality_incidents", "Success", items.Count,
            new { status, stationId, owner, keyword, take, skip }, cancellationToken).ConfigureAwait(false);
        return new QualityIncidentPageDto(result.Total, items);
    }

    [McpServerTool(Name = "get_quality_incident", ReadOnly = true)]
    [Description("读取一张质量异常单的详情、证据元数据与操作记录。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<QualityIncidentDetailsDto> GetQualityIncident(
        [Description("异常单主键")] long id,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var details = await incidents.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new McpException($"质量异常单 {id} 不存在。");
        var incident = details.Incident;
        var result = new QualityIncidentDetailsDto(
            incident.Id, incident.IncidentNumber, incident.Title, incident.Description,
            incident.Severity.ToString(), incident.Status.ToString(),
            incident.StationCode, incident.RecipeCode, incident.Owner, incident.CreatedBy,
            incident.CreatedAt, incident.UpdatedAt, incident.Version,
            details.Evidence.Select(e => new QualityIncidentEvidenceDto(
                e.RecordId, e.MonthKey, e.Purpose.ToString(), e.SerialNo, e.PalletCode,
                e.StationCode, e.RecipeCode, e.TriggerTime, e.Judgement.ToString(), e.ResultCode)).ToList(),
            details.Activities.Select(a => new QualityIncidentActivityDto(
                a.Kind.ToString(), a.FromStatus?.ToString(), a.ToStatus?.ToString(),
                a.CauseCategory, a.ActionTaken, a.VerificationResult, a.Comment,
                a.Actor, a.CreatedAt)).ToList());

        await audit.RecordAsync("get_quality_incident", "Success", 1, new { id },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static QualityIncidentStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }
        var normalized = status.Replace("_", "").Replace("-", "");
        return Enum.TryParse<QualityIncidentStatus>(normalized, ignoreCase: true, out var parsed)
            ? parsed
            : throw new McpException("status 只能是 Open、InProgress、PendingReview 或 Closed。");
    }
}
