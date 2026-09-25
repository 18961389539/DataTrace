using DataTrace.Application.Configuration;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Infrastructure.Reporting;

/// <summary>
/// 波形基线分析：从运行库取历史波形特征建立模板，再给最近样本打偏离分。
/// 取数走窄投影查询，统计全部在 <see cref="CurveTemplateBuilder"/> / <see cref="CurveTemplateMatcher"/> 里。
/// </summary>
public sealed class CurveTemplateService : ICurveTemplateService
{
    private readonly IRuntimeStore _store;
    private readonly IConfigRepository _config;

    public CurveTemplateService(IRuntimeStore store, IConfigRepository config)
    {
        _store = store;
        _config = config;
    }

    public async Task<CurveBaselineReport?> GetBaselineAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int recentCount = 30,
        int maxSamples = 2000,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var curve = snapshot.Stations
            .SelectMany(s => s.Curves)
            .FirstOrDefault(c => c.Id == curveDefinitionId);
        if (curve is null)
        {
            return null;
        }

        var series = ResolveSeries(curve, seriesName);
        var effectiveName = series?.Name ?? seriesName ?? "";

        // 型号隔离：不同型号的正常波形分布本来就不同（压力上限、保压时长都不一样），
        // 混在一起建模板会让切换型号后的每一条都被判成异常。
        // 范围与采集端共用 CurveRecipeScope：改过编码的型号要把历史旧码也算进来，
        // 否则记录上带着偏离分、这个页面却说一条样本都没有。
        var activeRecipeCode = snapshot.ActiveRecipe?.Code ?? "";
        var allowedRecipeCodes = CurveRecipeScope.AllowedCodes(activeRecipeCode, snapshot.ActiveRecipe?.PreviousCodes);

        // 基线要反映"最近"的正常状态，而不是整段历史的平均，所以按样本量设上限。
        // 型号过滤必须一起下推到 SQL（先过滤再取最新 N 条），否则另一种型号
        // 最近产量大一点就会把本型号样本挤出去，模板直接建不起来。
        var take = Math.Max(recentCount, maxSamples);
        var points = await _store
            .QueryCurveFeaturesAsync(
                curve.Id,
                string.IsNullOrWhiteSpace(effectiveName) ? null : effectiveName,
                from,
                to,
                take,
                allowedRecipeCodes,
                cancellationToken)
            .ConfigureAwait(false);

        // 区间内的型号分布按真数统计（不受 take 截断影响），用于解释"型号不匹配"；
        // 顺便让"取到多少条样本"是个真实数字而不是截断后的数字。
        var byRecipe = await _store
            .CountCurveFeaturesByRecipeAsync(
                curve.Id,
                string.IsNullOrWhiteSpace(effectiveName) ? null : effectiveName,
                from,
                to,
                cancellationToken)
            .ConfigureAwait(false);
        var totalSampleCount = byRecipe.Sum(x => x.Total);
        var inScope = byRecipe.Where(x => allowedRecipeCodes.Contains(x.RecipeCode, StringComparer.Ordinal)).ToList();
        var mismatched = totalSampleCount - inScope.Sum(x => x.Total);

        // 基线只用合格样本：历史里的不良波形正是我们要检出的东西，不能拿它当"正常"。
        var goodSamples = points.Where(p => !p.IsNg).Select(p => p.Feature).ToList();
        var template = CurveTemplateBuilder.Build(goodSamples);

        var recent = points
            .OrderByDescending(p => p.Time)
            .Take(recentCount)
            .OrderBy(p => p.Time)
            .ToList();

        var scores = recent
            .Select(p => CurveTemplateMatcher.Score(template, p.Feature, p.CurveRecordId, p.Time, p.PalletCode, p.IsNg))
            .ToList();

        return new CurveBaselineReport
        {
            CurveDefinitionId = curve.Id,
            CurveCode = curve.Code,
            CurveName = curve.Name,
            SeriesName = effectiveName,
            Role = series?.Role ?? points.FirstOrDefault()?.Role ?? SeriesRole.Y,
            Unit = series?.Unit,
            TotalSampleCount = totalSampleCount,
            MismatchedRecipeCount = mismatched,
            BaselineSampleCount = goodSamples.Count,
            RecipeCode = activeRecipeCode,
            NgCount = inScope.Sum(x => x.Ng),
            Template = template,
            Recent = scores,
            Shadow = Compare(scores),
            EmptyReason = BuildEmptyReason(mismatched, inScope.Sum(x => x.Total), goodSamples.Count, template, activeRecipeCode)
        };
    }

    /// <summary>
    /// 把偏离分的判定与实际判定交叉成四格。
    /// 只有拿到偏离结论的样本参与统计 —— 基线不可用的那些不是"判定为正常"，而是"没判"。
    /// </summary>
    private static CurveShadowComparison Compare(IReadOnlyList<CurveTemplateScore> scores)
    {
        var scored = scores
            .Where(s => s.Verdict != CurveTemplateVerdict.InsufficientBaseline)
            .ToList();

        return new CurveShadowComparison
        {
            Checked = scored.Count,
            TruePositive = scored.Count(s => s.Verdict == CurveTemplateVerdict.Abnormal && s.IsNg),
            FalsePositive = scored.Count(s => s.Verdict == CurveTemplateVerdict.Abnormal && !s.IsNg),
            FalseNegative = scored.Count(s => s.Verdict != CurveTemplateVerdict.Abnormal && s.IsNg),
            TrueNegative = scored.Count(s => s.Verdict != CurveTemplateVerdict.Abnormal && !s.IsNg)
        };
    }

    private static string? BuildEmptyReason(
        int mismatched,
        int inScopeTotal,
        int goodSamples,
        CurveTemplate template,
        string activeRecipeCode)
    {
        if (inScopeTotal == 0)
        {
            if (mismatched > 0)
            {
                var scope = string.IsNullOrEmpty(activeRecipeCode)
                    ? "本型号（当前未选型号）"
                    : $"本型号 {activeRecipeCode}（含它改码前的编码）";
                return $"该区间内没有属于{scope}的样本，另有 {mismatched} 条属于其他型号。" +
                       "不同型号的波形分布不同，混用会让基线失去意义。";
            }

            return "该区间内没有采样数据，请放宽时间范围。";
        }

        if (goodSamples == 0)
        {
            return $"该区间内本型号的 {inScopeTotal} 条样本全部不合格，没有可用于建立基线的合格样本。";
        }

        // 模板不可靠时把原因透出（样本不足 / 全部维度零波动），而不是让界面显示一个哑掉的分数。
        return template.IsReliable ? null : template.Note;
    }

    /// <summary>
    /// 解析目标序列：指定名称时精确匹配；留空时落到主序列（优先 Y 角色），与曲线判据的约定一致。
    /// </summary>
    private static CurveSeries? ResolveSeries(CurveDefinition curve, string? seriesName)
    {
        if (curve.Series.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(seriesName))
        {
            return curve.Series.FirstOrDefault(s => string.Equals(s.Name, seriesName, StringComparison.Ordinal));
        }

        return curve.Series
            .OrderBy(s => s.Role == SeriesRole.Y ? 0 : 1)
            .First();
    }
}
