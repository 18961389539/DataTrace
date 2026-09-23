using DataTrace.Application.Configuration;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Infrastructure.Reporting;

/// <summary>
/// 过程能力分析：规格限来自配置库（点位定义），样本值来自运行库的窄投影查询。
/// 计算本身全部在 <see cref="SpcCalculator"/> / <see cref="SpcRuleEvaluator"/> 里，本类只负责取数与组装。
/// </summary>
public sealed class SpcService : ISpcService
{
    private readonly IRuntimeStore _store;
    private readonly IConfigRepository _config;

    public SpcService(IRuntimeStore store, IConfigRepository config)
    {
        _store = store;
        _config = config;
    }

    public async Task<ProcessCapabilityReport?> GetProcessCapabilityAsync(
        int tagId,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var tag = snapshot.Stations.SelectMany(s => s.Tags).FirstOrDefault(t => t.Id == tagId);
        if (tag is null)
        {
            return null;
        }

        // 窄投影已按 TagId 在服务端过滤；但跨月拼接后必须重新按时间排序，
        // 否则移动极差会算到"上个月最后一点与本月第一点"这种假相邻关系上。
        var points = (await _store.QueryTagTrendAsync(from, to, tagId, cancellationToken).ConfigureAwait(false))
            .OrderBy(p => p.Time)
            .ToList();

        var values = points.Select(p => p.Value).ToList();

        // 必须用生效限值（点位默认限值 + 当前型号覆盖），否则会出现
        // "采集按型号限值判废、报表按点位默认限值算 Cpk"这种口径不一致。
        var limits = RecipeLimitResolver.Resolve(tag, snapshot.ActiveRecipe);
        var summary = SpcCalculator.Compute(values, limits.Lower, limits.Upper, limits.Target);
        var violations = SpcRuleEvaluator.Evaluate(values, summary);

        return new ProcessCapabilityReport
        {
            TagId = tag.Id,
            TagCode = tag.Code,
            TagName = tag.Name,
            Unit = tag.Unit,
            LowerLimit = limits.Lower,
            UpperLimit = limits.Upper,
            TargetValue = limits.Target,
            RecipeCode = snapshot.ActiveRecipe?.Code ?? "",
            Samples = points
                .Select(p => new TrendPoint { Time = p.Time, Value = p.Value, PalletCode = p.PalletCode })
                .ToList(),
            Summary = summary,
            Violations = violations
        };
    }
}
