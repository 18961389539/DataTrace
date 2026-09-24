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
        var points = await _store.QueryJudgementPointsAsync(from, to, stationId, recipeCode, cancellationToken).ConfigureAwait(false);

        var byDay = points
            .GroupBy(p => p.Time.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyThroughput
            {
                Day = g.Key,
                Total = g.Count(),
                Ok = g.Count(x => x.Judgement == Judgement.Ok),
                Ng = g.Count(x => x.Judgement == Judgement.Ng),
                None = g.Count(x => x.Judgement == Judgement.None)
            })
            .ToList();

        var byRecipe = points
            .GroupBy(p => p.RecipeCode ?? "")
            .OrderBy(g => string.IsNullOrEmpty(g.Key) ? "~" : g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RecipeThroughput
            {
                RecipeCode = g.Key,
                Total = g.Count(),
                Ok = g.Count(x => x.Judgement == Judgement.Ok),
                Ng = g.Count(x => x.Judgement == Judgement.Ng),
                Pending = g.Count(x => x.Judgement == Judgement.None)
            })
            .ToList();

        return new ThroughputReport { ByDay = byDay, ByRecipe = byRecipe };
    }

    public async Task<IReadOnlyList<IssueTopItem>> GetDefectTopAsync(DateTime from, DateTime to, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        var points = await _store.QueryOutOfLimitTagsAsync(from, to, recipeCode, cancellationToken).ConfigureAwait(false);
        return Top(points, take);
    }

    public async Task<IReadOnlyList<IssueTopItem>> GetWarningTopAsync(DateTime from, DateTime to, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default)
    {
        var points = await _store.QueryWarningTagsAsync(from, to, recipeCode, cancellationToken).ConfigureAwait(false);
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
                PalletCode = x.PalletCode
            })
            .ToList();
    }

    /// <summary>按点位名称聚合计数，降序取前 N。名称缺失时回退到编码。</summary>
    private static IReadOnlyList<IssueTopItem> Top(IReadOnlyList<TagIssuePoint> points, int take)
        => points
            .GroupBy(t => string.IsNullOrWhiteSpace(t.TagName) ? t.TagCode : t.TagName)
            .Select(g => new IssueTopItem { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(take)
            .ToList();
}
