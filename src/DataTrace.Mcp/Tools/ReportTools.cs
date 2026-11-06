using System.ComponentModel;
using DataTrace.Application.Reporting;
using DataTrace.Domain.Evaluation;
using DataTrace.Mcp.Models;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DataTrace.Mcp.Tools;

[McpServerToolType]
public sealed class ReportTools(
    IReportService reports,
    ISpcService spc,
    McpHeavyQueryGate gate,
    McpCallContext call,
    ToolAudit audit)
{
    private const int TrendOutputPoints = 200;

    [McpServerTool(Name = "get_report", ReadOnly = true)]
    [Description("生成生产报表。kind 可选：throughput（产量）、piece_yield（直通率）、shift_stops（停线）、defect_top（不良 Top）、warning_top（预警 Top）。默认统计最近 24 小时，范围最多 31 天。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<ReportResultDto> GetReport(
        [Description("报表类型：throughput / piece_yield / shift_stops / defect_top / warning_top")] string kind,
        [Description("ISO 8601 起始时间，含时区偏移；缺省为 to 前 24 小时")] DateTimeOffset? from = null,
        [Description("ISO 8601 截止时间，含时区偏移；缺省为当前时间")] DateTimeOffset? to = null,
        [Description("工站主键；不传表示全部工站（停线报表不支持）")] int? stationId = null,
        [Description("型号编码；空串表示仅未选型号")] string? recipeCode = null,
        [Description("Top N 的条数，默认 10，最大 50")] int take = 10,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ResolveRange(from, to);
        var normalized = (kind ?? "").Trim().ToLowerInvariant();
        var topN = Math.Clamp(take, 1, 50);
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);

        ReportResultDto result = normalized switch
        {
            "throughput" => new ReportResultDto(
                normalized,
                ToDto(await reports.GetThroughputAsync(fromLocal, toLocal, stationId, recipeCode, cancellationToken)
                    .ConfigureAwait(false)),
                null, null, null),
            "piece_yield" => new ReportResultDto(
                normalized, null,
                ToDto(await reports.GetPieceYieldAsync(fromLocal, toLocal, stationId, recipeCode, cancellationToken)
                    .ConfigureAwait(false)),
                null, null),
            "shift_stops" => new ReportResultDto(
                normalized, null, null,
                (await reports.GetShiftStopsAsync(fromLocal, toLocal, cancellationToken).ConfigureAwait(false))
                    .ByShift.Select(ToDto).ToList(),
                null),
            "defect_top" => new ReportResultDto(
                normalized, null, null, null,
                ToDto(await reports.GetDefectTopAsync(fromLocal, toLocal, stationId, topN, recipeCode, cancellationToken)
                    .ConfigureAwait(false))),
            "warning_top" => new ReportResultDto(
                normalized, null, null, null,
                ToDto(await reports.GetWarningTopAsync(fromLocal, toLocal, stationId, topN, recipeCode, cancellationToken)
                    .ConfigureAwait(false))),
            _ => throw new McpException(
                "kind 只能是 throughput、piece_yield、shift_stops、defect_top 或 warning_top。")
        };

        await audit.RecordAsync("get_report", "Success", 1, new
        {
            kind = normalized, fromLocal, toLocal, stationId, recipeCode, take = topN
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    [McpServerTool(Name = "get_trend", ReadOnly = true)]
    [Description("读取某个点位的数值趋势，按时间升序。最多返回 200 个点，超出时等距降采样并标注 downsampled。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<TrendResultDto> GetTrend(
        [Description("点位主键，可从 list_tags 获取")] int tagId,
        [Description("ISO 8601 起始时间，含时区偏移；缺省为 to 前 24 小时")] DateTimeOffset? from = null,
        [Description("ISO 8601 截止时间，含时区偏移；缺省为当前时间")] DateTimeOffset? to = null,
        [Description("型号编码；空串表示仅未选型号")] string? recipeCode = null,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ResolveRange(from, to);
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var points = await reports.GetTrendAsync(
            fromLocal, toLocal, tagId, recipeCode, call.MaxTrendPoints, cancellationToken).ConfigureAwait(false);

        var downsampled = points.Count > TrendOutputPoints;
        var output = downsampled ? Downsample(points, TrendOutputPoints) : points;
        var result = new TrendResultDto(
            tagId,
            fromLocal,
            toLocal,
            points.Count,
            downsampled,
            output.Select(p => new TrendPointDto(
                p.Time, p.Value, p.PalletCode, p.LowerLimit, p.UpperLimit)).ToList());

        await audit.RecordAsync("get_trend", "Success", result.Points.Count,
            new { tagId, fromLocal, toLocal, recipeCode }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    [McpServerTool(Name = "get_process_capability", ReadOnly = true)]
    [Description("计算某点位的过程能力与判异结果。按规格限变化分段返回 Cp/Cpk/Pp/Ppk、控制限与判异记录，不返回原始采样数组。")]
    [Authorize(Policy = McpPolicies.Read)]
    public async Task<ProcessCapabilityDto> GetProcessCapability(
        [Description("点位主键，可从 list_tags 获取")] int tagId,
        [Description("ISO 8601 起始时间，含时区偏移；缺省为 to 前 24 小时")] DateTimeOffset? from = null,
        [Description("ISO 8601 截止时间，含时区偏移；缺省为当前时间")] DateTimeOffset? to = null,
        [Description("型号编码；空串表示仅未选型号")] string? recipeCode = null,
        CancellationToken cancellationToken = default)
    {
        call.RequireRead();
        var (fromLocal, toLocal) = call.ResolveRange(from, to);
        using var lease = await gate.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var report = await spc.GetProcessCapabilityAsync(
            tagId, fromLocal, toLocal, recipeCode, call.MaxSpcSamples, cancellationToken).ConfigureAwait(false)
            ?? throw new McpException($"点位 {tagId} 不存在。");

        var result = new ProcessCapabilityDto(
            report.TagId,
            report.TagName,
            report.Unit,
            report.RecipeCode,
            report.TargetValue,
            report.Verdict.ToString(),
            report.SpecChangedInWindow,
            report.Segments.Select(s => new SpcSegmentDto(
                s.Number, s.StartTime, s.EndTime, s.LowerLimit, s.UpperLimit,
                s.LimitsFromConfig, s.Count, ToDto(s.Summary),
                s.Violations.Select(ToDto).ToList())).ToList(),
            report.Violations.Select(ToDto).ToList());

        await audit.RecordAsync("get_process_capability", "Success", result.Segments.Count,
            new { tagId, fromLocal, toLocal, recipeCode }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static List<TrendPoint> Downsample(IReadOnlyList<TrendPoint> points, int target)
    {
        var step = (int)Math.Ceiling(points.Count / (double)target);
        var result = new List<TrendPoint>(target + 1);
        for (var i = 0; i < points.Count; i += step)
        {
            result.Add(points[i]);
        }
        if (result[^1] != points[^1])
        {
            result.Add(points[^1]);
        }
        return result;
    }

    private static ThroughputReportDto ToDto(ThroughputReport report)
        => new(report.ByDay.Select(ToDto).ToList(), report.ByRecipe.Select(ToDto).ToList());

    private static DailyThroughputDto ToDto(DailyThroughput row)
        => new(row.Day, row.ShiftLabel, row.Total, row.Ok, row.Ng,
            row.QualityNg, row.CollectNg, row.None, row.FirstPassYield);

    private static RecipeThroughputDto ToDto(RecipeThroughput row)
        => new(row.RecipeCode, row.Total, row.Ok, row.Ng, row.Pending, row.PassRate);

    private static PieceYieldReportDto ToDto(PieceYieldReport report)
        => new(
            report.ByShift.Select(ToDto).ToList(),
            report.ByRecipe.Select(ToDto).ToList(),
            report.Drag is { } drag
                ? new PieceYieldDragDto(drag.StationCode, drag.PointName, drag.PieceCount, drag.HighCount, drag.LowCount)
                : null,
            report.FirstNg is { } first
                ? new PieceFirstNgDto(first.StationCode, first.PieceCount)
                : null,
            report.Clearance is { } clearance
                ? new SpecClearanceDto(clearance.Nearest, clearance.NearCount, clearance.HasBand, clearance.Text)
                : null);

    private static ShiftStopDto ToDto(ShiftStopSummary row)
        => new(row.ShiftStart, row.HadPasses, row.WholeLine, row.StationCode,
            (int)Math.Round(row.Duration.TotalMinutes), row.LostPieces, row.StopCount,
            row.TotalLostPieces, row.Detail, row.Brief);

    private static IssueTopDto ToDto(IssueTopReport report)
        => new(
            report.Items.Select(i => new IssueTopItemDto(i.Name, i.Count)).ToList(),
            report.Total,
            report.MissingItems.Select(i => new IssueTopItemDto(i.Name, i.Count)).ToList(),
            report.MissingTotal);

    private static SpcSummaryDto ToDto(SpcSummary summary)
        => new(
            summary.Count, summary.Mean, summary.Min, summary.Max,
            summary.OverallStdDev, summary.WithinStdDev,
            summary.Cp, summary.Cpk, summary.Pp, summary.Ppk,
            summary.CenterLine, summary.UpperControlLimit, summary.LowerControlLimit,
            summary.ControlLimitsFrozen, summary.Verdict.ToString(), summary.Note);

    private static SpcViolationDto ToDto(SpcViolation violation)
        => new(violation.Rule.ToString(), violation.Description, violation.StartIndex, violation.EndIndex);
}
