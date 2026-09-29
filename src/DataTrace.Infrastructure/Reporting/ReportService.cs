using DataTrace.Application.Configuration;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Reporting;

/// <summary>
/// 报表统计。
/// 取数走存储层的窄投影查询（服务端过滤 + 只取用得到的列），不再把整条记录图
/// （Products / TagValues 全字段）加载进内存；聚合在这些极小的结果集上完成。
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly IRuntimeAnalytics _store;
    private readonly IConfigRepository? _config;

    public ReportService(IRuntimeAnalytics store, IConfigRepository? config = null)
    {
        _store = store;
        _config = config;
    }

    public async Task<ThroughputReport> GetThroughputAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        // 按日与按型号是同一次取数上的两种分组：分两次查会把区间内全部记录查两遍。
        // 计数已经在 SQL 侧数好（日 × 型号 × 判定），这里只是把格子摊到两张表上。
        var counts = await _store.CountJudgementsAsync(from, to, stationId, recipeCode, cancellationToken).ConfigureAwait(false);
        var (startHour, lengthHours) = await ShiftAsync(cancellationToken).ConfigureAwait(false);

        var byDay = counts
            .GroupBy(x => ShiftWindow.Containing(x.Day.Date.AddHours(x.Hour), startHour, lengthHours).Start)
            .OrderBy(g => g.Key)
            .Select(g => new DailyThroughput
            {
                Day = g.Key,
                ShiftLabel = new ShiftWindow(g.Key, g.Key.AddHours(lengthHours)).Label,
                Total = g.Sum(x => x.Count),
                Ok = g.Where(x => x.Judgement == Judgement.Ok).Sum(x => x.Count),
                Ng = g.Where(x => x.Judgement == Judgement.Ng).Sum(x => x.Count),
                None = g.Where(x => x.Judgement == Judgement.None).Sum(x => x.Count)
            })
            .ToList();

        var byRecipe = counts
            .GroupBy(x => x.RecipeCode)
            .OrderBy(g => string.IsNullOrEmpty(g.Key) ? "~" : g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecipeThroughput
            {
                RecipeCode = g.Key,
                Total = g.Sum(x => x.Count),
                Ok = g.Where(x => x.Judgement == Judgement.Ok).Sum(x => x.Count),
                Ng = g.Where(x => x.Judgement == Judgement.Ng).Sum(x => x.Count),
                Pending = g.Where(x => x.Judgement == Judgement.None).Sum(x => x.Count)
            })
            .ToList();

        return new ThroughputReport { ByDay = byDay, ByRecipe = byRecipe };
    }

    public async Task<PieceYieldReport> GetPieceYieldAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        var pieces = await _store.ListFinishedPiecesAsync(from, to, recipeCode, cancellationToken).ConfigureAwait(false);
        var ordered = await WithPieceOrderAsync(pieces, cancellationToken).ConfigureAwait(false);
        var (startHour, lengthHours) = await ShiftAsync(cancellationToken).ConfigureAwait(false);
        var report = PieceYieldRules.Summarize(ordered, stationId, startHour, lengthHours);
        if (report.Drag is not { } drag
            || string.IsNullOrWhiteSpace(drag.PointName)
            || drag.PointName == "不合格")
        {
            return report;
        }

        var readings = await _store.ListInSpecReadingsAsync(
            from, to, drag.StationCode, drag.PointName, stationId, recipeCode, cancellationToken).ConfigureAwait(false);
        return new PieceYieldReport
        {
            ByShift = report.ByShift,
            ByRecipe = report.ByRecipe,
            Drag = report.Drag,
            FirstNg = report.FirstNg,
            Clearance = SpecClearanceRules.Of(readings)
        };
    }

    public async Task<ShiftStopReport> GetShiftStopsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        var (startHour, lengthHours) = await ShiftAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.Now;
        var asOf = to < now ? to : now;
        var horizon = TimeSpan.FromHours(lengthHours);
        var loadFrom = from - horizon;
        var loadTo = asOf + horizon;
        if (loadTo > now)
        {
            loadTo = now;
        }

        if (loadFrom > loadTo)
        {
            loadFrom = loadTo;
        }

        var passes = await _store.ListStationPassesAsync(loadFrom, loadTo, cancellationToken).ConfigureAwait(false);
        var ordered = await WithStationOrderAsync(passes, cancellationToken).ConfigureAwait(false);
        return StationStopRules.Summarize(ordered, from, to, asOf, startHour, lengthHours);
    }

    private async Task<IReadOnlyList<FinishedPieceObservation>> WithPieceOrderAsync(
        IReadOnlyList<FinishedPieceObservation> pieces,
        CancellationToken cancellationToken)
    {
        if (_config is null || pieces.Count == 0)
        {
            return pieces;
        }

        var stations = (await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Stations
            .ToDictionary(station => station.Id);
        return pieces.Select(piece => new FinishedPieceObservation
        {
            MonthKey = piece.MonthKey,
            SessionId = piece.SessionId,
            EndTime = piece.EndTime,
            Judgement = piece.Judgement,
            RecipeCode = piece.RecipeCode,
            Stations = piece.Stations.Select(mark =>
            {
                stations.TryGetValue(mark.StationId, out var station);
                var sequence = station?.Sequence ?? (mark.Sequence != 0 ? mark.Sequence : mark.StationId);
                return mark with { Sequence = sequence };
            }).ToList(),
            Faults = piece.Faults
        }).ToList();
    }

    private async Task<IReadOnlyList<StationPass>> WithStationOrderAsync(
        IReadOnlyList<StationPass> passes,
        CancellationToken cancellationToken)
    {
        if (_config is null || passes.Count == 0)
        {
            return passes;
        }

        var stations = (await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Stations
            .ToDictionary(station => station.Id);
        return passes.Select(pass =>
        {
            stations.TryGetValue(pass.StationId, out var station);
            return new StationPass
            {
                MonthKey = pass.MonthKey,
                SessionId = pass.SessionId,
                StationId = pass.StationId,
                StationCode = pass.StationCode,
                Sequence = station?.Sequence ?? pass.Sequence,
                IsLastStation = station?.IsLastStation ?? pass.IsLastStation,
                CompleteTime = pass.CompleteTime
            };
        }).ToList();
    }

    private async Task<(int StartHour, int LengthHours)> ShiftAsync(CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            return (SystemDefaults.ShiftStartHour, SystemDefaults.ShiftLengthHours);
        }

        var settings = (await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).Settings;
        ShiftWindow.Normalize(settings.ShiftStartHour, settings.ShiftLengthHours, out var hour, out var length);
        return (hour, length);
    }

    public async Task<IssueTopReport> GetDefectTopAsync(DateTime from, DateTime to, int? stationId, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        var points = await _store.QueryOutOfLimitTagsAsync(from, to, stationId, recipeCode, cancellationToken).ConfigureAwait(false);
        var exceeded = Top(points.Where(point => !point.Missing).ToList(), take);
        var missing = Top(points.Where(point => point.Missing).ToList(), take);
        return new IssueTopReport
        {
            Items = exceeded.Items,
            Total = exceeded.Total,
            MissingItems = missing.Items,
            MissingTotal = missing.Total
        };
    }

    public async Task<IssueTopReport> GetWarningTopAsync(DateTime from, DateTime to, int? stationId, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        var points = await _store.QueryWarningTagsAsync(from, to, stationId, recipeCode, cancellationToken).ConfigureAwait(false);
        return Top(points, take);
    }

    public async Task<IReadOnlyList<TrendPoint>> GetTrendAsync(DateTime from, DateTime to, int tagId, string? recipeCode = null, int take = 0, CancellationToken cancellationToken = default)
    {
        var points = await _store.QueryTagTrendAsync(from, to, tagId, recipeCode, take, cancellationToken).ConfigureAwait(false);
        return points
            .OrderBy(x => x.Time)
            .Select(x => new TrendPoint
            {
                Time = x.Time,
                Value = x.Value,
                PalletCode = x.PalletCode,
                // 规格限跟着样本一起带出来：过程能力就是在这批点上按"限值有没有动过"分段的，
                // 不带的话统计服务只能再查一遍同一条窄投影。
                LowerLimit = x.LowerLimit,
                UpperLimit = x.UpperLimit
            })
            .ToList();
    }

    /// <summary>按点位名称聚合计数，降序取前 N；同时回带区间内全部次数（占比的分母）。</summary>
    private static IssueTopReport Top(IReadOnlyList<TagIssuePoint> points, int take)
        => new()
        {
            Items = points
                .GroupBy(t => t.TagName)
                .Select(g => new IssueTopItem { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Take(take)
                .ToList(),
            Total = points.Count
        };
}
