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

        // 最近样本是独立验证集，必须从训练窗口中完全排除，避免用训练数据验证自身。
        // 型号过滤一起下推到 SQL，确保截断前已排除其它型号。
        var evaluationLimit = Math.Max(0, recentCount);
        var baselineLimit = Math.Max(0, maxSamples);
        var take = (int)Math.Min(int.MaxValue, (long)evaluationLimit + baselineLimit);
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
        var ngCount = inScope.Sum(x => x.Ng);
        var unjudgedCount = inScope.Sum(x => x.Unjudged);

        var orderedPoints = points
            .OrderBy(p => p.Time)
            .ThenBy(p => p.CurveRecordId)
            .ToList();
        var recentStart = Math.Max(0, orderedPoints.Count - evaluationLimit);
        var recent = orderedPoints.Skip(recentStart).ToList();
        var training = orderedPoints.Take(recentStart).ToList();

        // 只用独立验证窗口之前的 OK 样本建模；NG 和未判定记录均不能代表正常状态。
        var goodSamples = training
            .Where(p => p.ActualJudgement == Judgement.Ok)
            .Select(p => p.Feature)
            .ToList();
        var template = CurveTemplateBuilder.Build(goodSamples);

        var scores = recent
            .Select(p => CurveTemplateMatcher.Score(
                template, p.Feature, p.CurveRecordId, p.Time, p.PalletCode, p.ActualJudgement))
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
            NgCount = ngCount,
            UnjudgedCount = unjudgedCount,
            Template = template,
            Recent = scores,
            Shadow = Compare(scores),
            EmptyReason = BuildEmptyReason(
                mismatched,
                inScope.Sum(x => x.Total),
                goodSamples.Count,
                ngCount,
                unjudgedCount,
                template,
                activeRecipeCode)
        };
    }

    /// <summary>
    /// 把偏离分的判定与实际判定交叉成四格。
    /// 只有拿到偏离结论的样本参与统计 —— 基线不可用的那些不是"判定为正常"，而是"没判"。
    /// </summary>
    private static CurveShadowComparison Compare(IReadOnlyList<CurveTemplateScore> scores)
    {
        var scored = scores
            .Where(s => s.Verdict != CurveTemplateVerdict.InsufficientBaseline
                        && (s.ActualJudgement is Judgement.Ok or Judgement.Ng))
            .ToList();

        return new CurveShadowComparison
        {
            Checked = scored.Count,
            TruePositive = scored.Count(s => s.Verdict == CurveTemplateVerdict.Abnormal
                                             && s.ActualJudgement == Judgement.Ng),
            FalsePositive = scored.Count(s => s.Verdict == CurveTemplateVerdict.Abnormal
                                              && s.ActualJudgement == Judgement.Ok),
            FalseNegative = scored.Count(s => s.Verdict != CurveTemplateVerdict.Abnormal
                                              && s.ActualJudgement == Judgement.Ng),
            TrueNegative = scored.Count(s => s.Verdict != CurveTemplateVerdict.Abnormal
                                             && s.ActualJudgement == Judgement.Ok)
        };
    }

    private static string? BuildEmptyReason(
        int mismatched,
        int inScopeTotal,
        int goodSamples,
        int ngCount,
        int unjudgedCount,
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
            if (ngCount + unjudgedCount == inScopeTotal)
            {
                return $"该区间内本型号的 {ngCount} 条 NG 和 {unjudgedCount} 条未判定样本均不参与建基线；" +
                       "请检查近期样本，或扩大时间范围寻找更早的 OK 样本。";
            }

            return "最新样本保留作独立评估；其之前没有可用于建立基线的 OK 样本，请扩大时间范围。";
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
