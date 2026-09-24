using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Collector;

/// <summary>
/// 从历史特征构建一整批波形基线。
/// </summary>
/// <remarks>
/// 单独抽出来是为了可测：定时调度那部分（<see cref="CurveBaselineRefresher"/>）没什么逻辑，
/// 但"哪些序列能进基线"有明确规则 —— 样本要够、要合格、型号要对，漏一条就会让现场看到
/// 一堆来源不明的分数。
/// </remarks>
public sealed class CurveBaselineFactory
{
    /// <summary>基线回溯窗口：只看最近这段时间的波形，反映设备"现在"的正常状态。</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    /// <summary>每个序列最多取多少条样本参与建基线。</summary>
    public const int MaxSamplesPerSeries = 500;

    private readonly IConfigRepository _config;
    private readonly IRuntimeStore _store;

    public CurveBaselineFactory(IConfigRepository config, IRuntimeStore store)
    {
        _config = config;
        _store = store;
    }

    /// <summary>为当前生效型号构建全部曲线序列的基线。<paramref name="to"/> 是回溯窗口的右端。</summary>
    public async Task<CurveBaselineSnapshot> BuildAsync(DateTime to, CancellationToken cancellationToken = default)
    {
        var snapshot = await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var from = to - Lookback;

        // 型号隔离：基线只由与当前生效型号一致的样本建立。
        // 不同型号的正常波形分布不同，混在一起会让切换型号后每条都报警。
        var recipeCode = snapshot.ActiveRecipe?.Code ?? "";
        // 改编码后历史样本仍写旧码：把 PreviousCodes 一并算作本型号样本来源，避免基线空窗。
        var allowedRecipeCodes = new HashSet<string>(StringComparer.Ordinal) { recipeCode };
        if (snapshot.ActiveRecipe?.PreviousCodes is { } previous && !string.IsNullOrWhiteSpace(previous))
        {
            foreach (var part in previous.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                allowedRecipeCodes.Add(part);
            }
        }

        var templates = new Dictionary<CurveBaselineKey, CurveTemplate>();

        foreach (var curve in snapshot.Stations.SelectMany(s => s.Curves).Where(c => c.Enabled))
        {
            foreach (var series in curve.Series)
            {
                if (string.IsNullOrWhiteSpace(series.Name))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var points = await _store
                    .QueryCurveFeaturesAsync(curve.Id, series.Name, from, to, MaxSamplesPerSeries, cancellationToken)
                    .ConfigureAwait(false);

                // 只有合格样本能当"正常"：历史里的不良波形正是要检出的东西。
                var good = points
                    .Where(p => !p.IsNg && allowedRecipeCodes.Contains(p.RecipeCode))
                    .Select(p => p.Feature)
                    .ToList();

                // 样本不足的直接不入缓存：留一个不可靠的模板，只会让每条记录都带一个
                // "仅供参考"的分数，反而削弱现场对这个数字的信任。
                if (good.Count < CurveTemplateBuilder.MinimumReliableSamples)
                {
                    continue;
                }

                var template = CurveTemplateBuilder.Build(good);
                if (template.IsReliable)
                {
                    templates[new CurveBaselineKey(curve.Id, series.Name)] = template;
                }
            }
        }

        return new CurveBaselineSnapshot
        {
            RecipeCode = recipeCode,
            RefreshedAt = to,
            Templates = templates
        };
    }
}
