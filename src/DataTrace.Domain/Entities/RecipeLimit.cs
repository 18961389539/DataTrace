namespace DataTrace.Domain.Entities;

/// <summary>
/// 型号对某个点位限值的覆盖行。每个字段留空表示"这一项沿用点位默认值"，
/// 而不是"这一项不判"——漏填不应该让一个判定项整体失效。
/// </summary>
public class RecipeLimit
{
    public int Id { get; set; }
    public int RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    /// <summary>被覆盖的点位主键。点位不存在时该行不生效。</summary>
    public int TagId { get; set; }

    /// <summary>规格下限覆盖值。</summary>
    public double? LowerLimit { get; set; }

    /// <summary>规格上限覆盖值。</summary>
    public double? UpperLimit { get; set; }

    /// <summary>预警下限覆盖值。</summary>
    public double? WarningLowerLimit { get; set; }

    /// <summary>预警上限覆盖值。</summary>
    public double? WarningUpperLimit { get; set; }

    /// <summary>目标值覆盖值（仅统计）。</summary>
    public double? TargetValue { get; set; }
}
