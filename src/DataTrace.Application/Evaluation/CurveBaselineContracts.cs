using DataTrace.Domain.Evaluation;

namespace DataTrace.Application.Evaluation;

/// <summary>基线缓存的键：曲线定义 + 序列名。</summary>
public readonly record struct CurveBaselineKey(int CurveDefinitionId, string SeriesName);

/// <summary>某一时刻生效的整套波形基线。</summary>
public sealed class CurveBaselineSnapshot
{
    /// <summary>建立这批基线时生效的产品型号；空串表示未选型号。</summary>
    public required string RecipeCode { get; init; }

    public required DateTime RefreshedAt { get; init; }

    /// <summary>键为曲线定义 + 序列，值为该序列的基线模板。</summary>
    public required IReadOnlyDictionary<CurveBaselineKey, CurveTemplate> Templates { get; init; }

    public int Count => Templates.Count;

    /// <summary>取某条序列的基线；没有则返回 null（调用方据此跳过打分，而不是造一个空模板）。</summary>
    public CurveTemplate? Find(int curveDefinitionId, string seriesName)
        => Templates.GetValueOrDefault(new CurveBaselineKey(curveDefinitionId, seriesName));
}

/// <summary>
/// 波形基线的运行时缓存。
/// </summary>
/// <remarks>
/// 基线是**聚合量**（需要几十条历史样本才能算出来），不可能在采集流水线里现算 ——
/// 那既要读历史库、又依赖数据积累。所以由后台服务预先建好放进这里，
/// 采集线程只做一次字典查找加一次纯计算，不产生任何 IO。
/// </remarks>
public interface ICurveBaselineCache
{
    /// <summary>当前生效的基线；后台服务尚未完成首次刷新时为 null。</summary>
    CurveBaselineSnapshot? Current { get; }

    /// <summary>用新一批基线整体替换（后台服务调用）。</summary>
    void Replace(CurveBaselineSnapshot snapshot);
}
