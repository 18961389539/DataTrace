using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Reporting;

/// <summary>
/// 报表统计。
/// 取数走存储层的窄投影查询（服务端过滤 + 只取用得到的列），不再把整条记录图
/// （Products / TagValues 全字段）加载进内存；聚合在这些极小的结果集上完成。
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly IRuntimeStore _store;

    public ReportService(IRuntimeStore store)
    {
        _store = store;
    }

    public async Task<ThroughputReport> GetThroughputAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        // 按日与按型号是同一次取数上的两种分组：分两次查会把区间内全部记录查两遍。
        // 计数已经在 SQL 侧数好（日 × 型号 × 判定），这里只是把格子摊到两张表上。
        var counts = await _store.CountJudgementsAsync(from, to, stationId, recipeCode, cancellationToken).ConfigureAwait(false);

        var byDay = counts
            .GroupBy(x => x.Day)
            .OrderBy(g => g.Key)
            .Select(g => new DailyThroughput
            {
                Day = g.Key,
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

    public async Task<IssueTopReport> GetDefectTopAsync(DateTime from, DateTime to, int? stationId, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        var points = await _store.QueryOutOfLimitTagsAsync(from, to, stationId, recipeCode, cancellationToken).ConfigureAwait(false);
        return Top(points, take);
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
                .GroupBy(t => string.IsNullOrWhiteSpace(t.TagName) ? t.TagCode : t.TagName)
                .Select(g => new IssueTopItem { Name = g.Key, Count = g.Count() })
                .OrderByDescending(x => x.Count)
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .Take(take)
                .ToList(),
            Total = points.Count
        };
}
