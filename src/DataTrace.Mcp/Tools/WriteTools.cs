using System.ComponentModel;
using DataTrace.Application.Alarms;
using DataTrace.Application.Quality;
using DataTrace.Domain.Enums;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

/// <summary>
/// 二期写入工具。没有 McpServerToolType 标记：只有 Mcp:AllowWrites=true 时由 Web 显式注册，
/// 关闭时连 tools/list 都不会出现。
/// </summary>
public sealed class WriteTools(
    IAlarmIncidents alarms,
    IQualityIncidentService incidents,
    McpCallContext call,
    ToolAudit audit)
{
    [McpServerTool(Name = "acknowledge_alarms", Destructive = false, Idempotent = true)]
    [Description("接手一条或多条报警。已接手的视为成功，不会重复写入。最多 50 条。需要 write scope 且服务端开启写入。")]
    [Authorize(Policy = McpPolicies.Write)]
    public async Task<AlarmAckDto> AcknowledgeAlarms(
        [Description("报警主键列表，最多 50 条")] long[] ids,
        CancellationToken cancellationToken = default)
    {
        call.RequireWritesEnabled();
        var distinct = (ids ?? []).Distinct().ToArray();
        if (distinct.Length == 0)
        {
            throw new McpException("ids 不能为空。");
        }
        if (distinct.Length > 50)
        {
            throw new McpException("一次最多接手 50 条报警。");
        }

        var result = await alarms.AcknowledgeManyAsync(distinct, call.UserName, cancellationToken)
            .ConfigureAwait(false);
        await audit.RecordAsync("acknowledge_alarms", "Success", result.Taken,
            new { ids = distinct, result.AlreadyTaken, result.Missing }, cancellationToken).ConfigureAwait(false);
        return new AlarmAckDto(result.Taken, result.AlreadyTaken, result.Missing, result.AuditError);
    }

    [McpServerTool(Name = "create_quality_incident", Destructive = true, Idempotent = true)]
    [Description("创建质量异常单。requestId 由客户端生成并在重试时保持不变，用于幂等。需要 write scope 且服务端开启写入。")]
    [Authorize(Policy = McpPolicies.Write)]
    public async Task<QualityIncidentMutationDto> CreateQualityIncident(
        [Description("客户端生成的幂等键 GUID")] Guid requestId,
        [Description("月份键 yyyyMM")] string monthKey,
        [Description("关联采集记录主键；不关联时传 0")] long recordId,
        [Description("标题")] string title,
        [Description("问题描述")] string description,
        [Description("严重度：Low / Medium / High / Critical")] string severity = "Medium",
        [Description("工站主键")] int? stationId = null,
        [Description("工站编码")] string? stationCode = null,
        [Description("型号编码")] string? recipeCode = null,
        [Description("关联报警主键")] long? sourceAlarmId = null,
        [Description("影响范围起始时间，ISO 8601 带偏移")] DateTimeOffset? scopeFrom = null,
        [Description("影响范围截止时间，ISO 8601 带偏移")] DateTimeOffset? scopeTo = null,
        CancellationToken cancellationToken = default)
    {
        call.RequireWritesEnabled();
        if (requestId == Guid.Empty)
        {
            throw new McpException("requestId 不能为空。");
        }
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
        {
            throw new McpException("title 与 description 不能为空。");
        }
        if (monthKey.Length != 6 || !monthKey.All(char.IsAsciiDigit))
        {
            throw new McpException("monthKey 必须是 yyyyMM 格式。");
        }

        var request = new CreateQualityIncidentRequest
        {
            RequestId = requestId,
            MonthKey = monthKey,
            RecordId = recordId,
            Title = title.Trim(),
            Description = description.Trim(),
            Severity = ParseSeverity(severity),
            StationId = stationId,
            StationCode = string.IsNullOrWhiteSpace(stationCode) ? null : stationCode.Trim(),
            RecipeCode = string.IsNullOrWhiteSpace(recipeCode) ? null : recipeCode.Trim(),
            SourceAlarmId = sourceAlarmId,
            ScopeFrom = scopeFrom?.ToLocalTime().DateTime,
            ScopeTo = scopeTo?.ToLocalTime().DateTime
        };

        long id;
        try
        {
            id = await incidents.CreateAsync(request, call.Actor, cancellationToken).ConfigureAwait(false);
        }
        catch (QualityIncidentConflictException ex)
        {
            throw new McpException($"该 requestId 已处理：{ex.Message}");
        }

        var details = await incidents.GetAsync(id, cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync("create_quality_incident", "Success", 1,
            new { id, requestId, monthKey, recordId, title, severity }, cancellationToken).ConfigureAwait(false);
        return new QualityIncidentMutationDto(
            id,
            details?.Incident.Version ?? 0,
            details?.Incident.Status.ToString() ?? QualityIncidentStatus.Open.ToString());
    }

    [McpServerTool(Name = "update_quality_incident", Destructive = true, Idempotent = false)]
    [Description("流转质量异常单：take 接手、assign 指派、add_note 记录措施、submit_review 提交复核、review 复核、reopen 重开。必须带 expectedVersion，冲突时返回错误。需要 write scope 且服务端开启写入。")]
    [Authorize(Policy = McpPolicies.Write)]
    public async Task<QualityIncidentMutationDto> UpdateQualityIncident(
        [Description("异常单主键")] long id,
        [Description("动作：take / assign / add_note / submit_review / review / reopen")] string action,
        [Description("客户端读到的版本号，用于并发冲突检测")] int expectedVersion,
        [Description("assign 的负责人用户名")] string? owner = null,
        [Description("add_note 的措施；submit_review 的措施")] string? actionTaken = null,
        [Description("备注；review 的复核意见；reopen 的原因")] string? comment = null,
        [Description("submit_review 的原因分类")] string? causeCategory = null,
        [Description("submit_review 的验证结果")] string? verificationResult = null,
        [Description("submit_review 关联的月份键 yyyyMM")] string? monthKey = null,
        [Description("submit_review 关联的记录主键")] long recordId = 0,
        [Description("review 是否通过")] bool approved = false,
        CancellationToken cancellationToken = default)
    {
        call.RequireWritesEnabled();
        var normalized = (action ?? "").Trim().ToLowerInvariant();
        try
        {
            switch (normalized)
            {
                case "take":
                    await incidents.TakeAsync(id, expectedVersion, call.Actor, cancellationToken).ConfigureAwait(false);
                    break;
                case "assign":
                    Require(owner, "owner");
                    await incidents.AssignAsync(id, owner!.Trim(), expectedVersion, call.Actor, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case "add_note":
                    Require(actionTaken, "actionTaken");
                    await incidents.AddWorkNoteAsync(id, new QualityIncidentActionCommand
                    {
                        ActionTaken = actionTaken!.Trim(),
                        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()
                    }, expectedVersion, call.Actor, cancellationToken).ConfigureAwait(false);
                    break;
                case "submit_review":
                    Require(causeCategory, "causeCategory");
                    Require(actionTaken, "actionTaken");
                    Require(verificationResult, "verificationResult");
                    Require(monthKey, "monthKey");
                    await incidents.SubmitForReviewAsync(id, new QualityIncidentReviewSubmission
                    {
                        CauseCategory = causeCategory!.Trim(),
                        ActionTaken = actionTaken!.Trim(),
                        VerificationResult = verificationResult!.Trim(),
                        MonthKey = monthKey!.Trim(),
                        RecordId = recordId
                    }, expectedVersion, call.Actor, cancellationToken).ConfigureAwait(false);
                    break;
                case "review":
                    Require(comment, "comment");
                    await incidents.ReviewAsync(id, new QualityIncidentReviewCommand
                    {
                        Approved = approved,
                        Comment = comment!.Trim()
                    }, expectedVersion, call.Actor, cancellationToken).ConfigureAwait(false);
                    break;
                case "reopen":
                    Require(comment, "comment");
                    await incidents.ReopenAsync(id, comment!.Trim(), expectedVersion, call.Actor, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new McpException(
                        "action 只能是 take、assign、add_note、submit_review、review 或 reopen。");
            }
        }
        catch (QualityIncidentOperationException ex)
        {
            throw new McpException(ex.Message);
        }

        var details = await incidents.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new McpException($"质量异常单 {id} 不存在。");
        await audit.RecordAsync("update_quality_incident", "Success", 1,
            new { id, action = normalized, expectedVersion, owner, approved }, cancellationToken)
            .ConfigureAwait(false);
        return new QualityIncidentMutationDto(
            details.Incident.Id,
            details.Incident.Version,
            details.Incident.Status.ToString());
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new McpException($"{name} 不能为空。");
        }
    }

    private static QualityIncidentSeverity ParseSeverity(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "medium" => QualityIncidentSeverity.Medium,
            "low" => QualityIncidentSeverity.Low,
            "high" => QualityIncidentSeverity.High,
            "critical" => QualityIncidentSeverity.Critical,
            _ => throw new McpException("severity 只能是 Low、Medium、High 或 Critical。")
        };
}
