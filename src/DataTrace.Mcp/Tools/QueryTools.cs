using System.ComponentModel;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class QueryTools(IRuntimeStore runtime, McpCallContext call, ToolAudit audit)
{
    [McpServerTool(Name = "query_records", ReadOnly = true)]
    [Description("按时间、工站、托盘码、流水号、判定、结果码、型号筛选采集明细并分页。时间范围最多 31 天，take 最大 100；默认最近 24 小时。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<RecordPageDto> QueryRecords(
        [Description("ISO 8601 起始时间，含时区偏移；缺省为 to 前 24 小时")] DateTimeOffset? from = null,
        [Description("ISO 8601 截止时间，含时区偏移；缺省为当前时间")] DateTimeOffset? to = null,
        [Description("工站主键")] int? stationId = null,
        [Description("托盘码精确匹配")] string? palletCode = null,
        [Description("流水号精确匹配")] string? serialNo = null,
        [Description("判定：ok / ng / none")] string? judgement = null,
        [Description("结果码，例如 11 表示质量判废")] short? resultCode = null,
        [Description("仅返回含超限点位的记录")] bool onlyOutOfLimit = false,
        [Description("型号编码；空串表示仅未选型号")] string? recipeCode = null,
        [Description("每页条数，默认 50，最大 100")] int take = 50,
        [Description("上一页返回的 nextCursor；首页不传")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ResolveRange(from, to);
        var pageSize = call.ClampTake(take);
        var result = await runtime.QueryAsync(new CollectQueryRequest
        {
            From = fromLocal,
            To = toLocal,
            StationId = stationId,
            PalletCode = string.IsNullOrWhiteSpace(palletCode) ? null : palletCode.Trim(),
            SerialNo = string.IsNullOrWhiteSpace(serialNo) ? null : serialNo.Trim(),
            Judgement = ParseJudgement(judgement),
            ResultCode = resultCode,
            OnlyOutOfLimit = onlyOutOfLimit,
            RecipeCode = recipeCode,
            Take = pageSize,
            AfterCursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor
        }, cancellationToken).ConfigureAwait(false);

        var items = result.Items.Select(x => ToDto(x.MonthKey, x.Record, includeChildren: false)).ToList();
        await audit.RecordAsync("query_records", "Success", items.Count, new
        {
            fromLocal, toLocal, stationId, palletCode, serialNo, judgement, resultCode, onlyOutOfLimit, recipeCode, take = pageSize
        }, cancellationToken).ConfigureAwait(false);
        return new RecordPageDto(result.Total, items, result.NextCursor);
    }

    [McpServerTool(Name = "get_record_detail", ReadOnly = true)]
    [Description("读取一条采集记录详情：标签值、判定与超限/预警状态、曲线摘要。不返回曲线原始采样数组。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<RecordDto> GetRecordDetail(
        [Description("月份键，格式 yyyyMM，可从查询结果的 monthKey 获取")] string monthKey,
        [Description("记录主键")] long recordId,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        ValidateMonthKey(monthKey);
        var record = await runtime.GetRecordAsync(monthKey, recordId, cancellationToken).ConfigureAwait(false)
            ?? throw new McpException($"记录 {monthKey}:{recordId} 不存在。");
        var dto = ToDto(monthKey, record, includeChildren: true);
        await audit.RecordAsync("get_record_detail", "Success", 1, new { monthKey, recordId },
            cancellationToken).ConfigureAwait(false);
        return dto;
    }

    [McpServerTool(Name = "query_sessions", ReadOnly = true)]
    [Description("按时间、托盘码、流水号、会话状态筛选托盘会话履历。时间范围最多 31 天，take 最大 100。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<SessionPageDto> QuerySessions(
        [Description("ISO 8601 起始时间，含时区偏移；缺省为 to 前 24 小时")] DateTimeOffset? from = null,
        [Description("ISO 8601 截止时间，含时区偏移；缺省为当前时间")] DateTimeOffset? to = null,
        [Description("托盘码精确匹配")] string? palletCode = null,
        [Description("流水号精确匹配")] string? serialNo = null,
        [Description("会话状态：open / closed / abnormal")] string? status = null,
        [Description("型号编码；空串表示仅未选型号")] string? recipeCode = null,
        [Description("每页条数，默认 50，最大 100")] int take = 50,
        [Description("跳过的条数")] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ResolveRange(from, to);
        var pageSize = call.ClampTake(take);
        var result = await runtime.QuerySessionsAsync(new SessionQueryRequest
        {
            From = fromLocal,
            To = toLocal,
            PalletCode = string.IsNullOrWhiteSpace(palletCode) ? null : palletCode.Trim(),
            SerialNo = string.IsNullOrWhiteSpace(serialNo) ? null : serialNo.Trim(),
            Status = ParseSessionStatus(status),
            RecipeCode = recipeCode,
            Skip = Math.Max(0, skip),
            Take = pageSize
        }, cancellationToken).ConfigureAwait(false);

        var items = result.Items.Select(x =>
        {
            var session = x.Session;
            return new SessionDto(
                x.MonthKey, session.Id, session.SerialNo, session.PalletCode,
                session.StartTime, session.EndTime, session.Status.ToString(),
                session.Judgement.ToString(), session.FirstNgStationCode, session.FirstNgAt,
                session.FirstNgResultCode, session.FirstNgReason, x.RecipeCode, x.RecordCount);
        }).ToList();

        await audit.RecordAsync("query_sessions", "Success", items.Count, new
        {
            fromLocal, toLocal, palletCode, serialNo, status, recipeCode, take = pageSize, skip
        }, cancellationToken).ConfigureAwait(false);
        return new SessionPageDto(result.Total, items);
    }

    private static RecordDto ToDto(string monthKey, CollectRecord record, bool includeChildren)
        => new(
            monthKey,
            record.Id,
            record.PalletSessionId,
            record.SerialNo,
            record.PalletCode,
            record.StationId,
            record.StationCode,
            record.TriggerTime,
            record.CompleteTime,
            record.DurationMs,
            record.ResultCode,
            record.Judgement.ToString(),
            record.RecipeCode,
            record.ErrorMessage,
            includeChildren
                ? record.TagValues.Select(t => new TagValueDto(
                    t.TagId, t.TagName, t.UnitSnapshot, t.NumericValue, t.TextValue,
                    t.IsOutOfLimit, t.IsWarning, t.LowerLimit, t.UpperLimit,
                    t.WarningLowerLimit, t.WarningUpperLimit)).ToList()
                : null,
            includeChildren
                ? record.Curves.Select(c => new CurveSummaryDto(
                    c.CurveCode, c.CurveName, c.PointCount, c.FileSize)).ToList()
                : null);

    private static Judgement? ParseJudgement(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "ok" => Judgement.Ok,
            "ng" => Judgement.Ng,
            "none" => Judgement.None,
            _ => throw new McpException("judgement 只能是 ok、ng 或 none。")
        };

    private static SessionStatus? ParseSessionStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "open" => SessionStatus.Open,
            "closed" => SessionStatus.Closed,
            "abnormal" => SessionStatus.Abnormal,
            _ => throw new McpException("status 只能是 open、closed 或 abnormal。")
        };

    private static void ValidateMonthKey(string monthKey)
    {
        if (monthKey.Length != 6 || !monthKey.All(char.IsAsciiDigit)
            || !DateTime.TryParseExact(monthKey, "yyyyMM", null,
                System.Globalization.DateTimeStyles.None, out _))
        {
            throw new McpException("monthKey 必须是 yyyyMM 格式，例如 202611。");
        }
    }
}
