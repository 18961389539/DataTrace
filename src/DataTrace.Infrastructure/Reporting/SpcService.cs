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

        // 配置里的生效限值（点位默认 + 当前型号覆盖）。它有两个用途：
        // 目标值（没有随记录落库），以及给"限值列还是 null"的老数据兜底。
        var configLimits = RecipeLimitResolver.Resolve(tag, snapshot.ActiveRecipe);

        // 按采集时落库的规格限切连续段：限值一动就断，每段各自算能力指数与控制限。
        var runs = new List<(double? Lower, double? Upper, bool FromConfig, List<TagTrendPoint> Points)>();
        foreach (var point in points)
        {
            // 两列都没值 = 那批数据还没有"限值随记录落库"，只能按当前配置估；
            // 相邻的同类点归同一段，因为它们本来就是同一个口径。
            var fromConfig = point.LowerLimit is null && point.UpperLimit is null;
            var lower = fromConfig ? configLimits.Lower : point.LowerLimit;
            var upper = fromConfig ? configLimits.Upper : point.UpperLimit;

            if (runs.Count > 0 && runs[^1].Lower == lower && runs[^1].Upper == upper && runs[^1].FromConfig == fromConfig)
            {
                runs[^1].Points.Add(point);
            }
            else
            {
                runs.Add((lower, upper, fromConfig, [point]));
            }
        }

        var segments = new List<ProcessCapabilitySegment>();
        var start = 0;
        foreach (var run in runs)
        {
            segments.Add(BuildSegment(segments.Count + 1, start, run, configLimits));
            start += run.Points.Count;
        }

        if (segments.Count == 0)
        {
            // 空区间也要有一段，界面才能照旧显示"区间内没有采样数据"而不是空白。
            segments.Add(BuildEmptySegment(configLimits, from, to));
        }

        return new ProcessCapabilityReport
        {
            TagId = tag.Id,
            TagCode = tag.Code,
            TagName = tag.Name,
            Unit = tag.Unit,
            TargetValue = configLimits.Target,
            RecipeCode = snapshot.ActiveRecipe?.Code ?? "",
            Samples = points
                .Select(p => new TrendPoint { Time = p.Time, Value = p.Value, PalletCode = p.PalletCode })
                .ToList(),
            Segments = segments
        };
    }

    private static ProcessCapabilitySegment BuildSegment(
        int number,
        int startIndex,
        (double? Lower, double? Upper, bool FromConfig, List<TagTrendPoint> Points) run,
        TagLimits configLimits)
    {
        var values = run.Points.Select(p => p.Value).ToList();
        var summary = SpcCalculator.Compute(values, run.Lower, run.Upper, configLimits.Target);

        return new ProcessCapabilitySegment
        {
            Number = number,
            StartIndex = startIndex,
            Count = run.Points.Count,
            StartTime = run.Points[0].Time,
            EndTime = run.Points[^1].Time,
            LowerLimit = run.Lower,
            UpperLimit = run.Upper,
            LimitsFromConfig = run.FromConfig,
            Summary = summary,
            // 判异序号是段内 0 起的，必须换算成全窗口序号，否则界面点托盘会点到别的段上。
            Violations = SpcRuleEvaluator.Evaluate(values, summary)
                .Select(v => v with
                {
                    StartIndex = v.StartIndex + startIndex,
                    EndIndex = v.EndIndex + startIndex
                })
                .ToList()
        };
    }

    private static ProcessCapabilitySegment BuildEmptySegment(TagLimits configLimits, DateTime from, DateTime to)
    {
        var summary = SpcCalculator.Compute([], configLimits.Lower, configLimits.Upper, configLimits.Target);
        return new ProcessCapabilitySegment
        {
            Number = 1,
            StartTime = from,
            EndTime = to,
            LowerLimit = configLimits.Lower,
            UpperLimit = configLimits.Upper,
            LimitsFromConfig = true,
            Summary = summary
        };
    }
}
