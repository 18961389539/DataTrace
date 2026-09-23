namespace DataTrace.Domain.Entities;

/// <summary>
/// 产品型号（配方）。同一个点位在不同型号下可以有不同的规格限/预警限，
/// 换型号时不再需要直接改动点位定义。
/// </summary>
/// <remarks>
/// 本轮只覆盖点位数值限值；曲线判据仍按曲线定义全型号共用（多一层覆盖表的收益尚不明确）。
/// </remarks>
public class Recipe
{
    public int Id { get; set; }

    /// <summary>型号编码，全局唯一。</summary>
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>停用的型号不能被设为当前型号。</summary>
    public bool Enabled { get; set; } = true;

    public string? Remark { get; set; }

    public ICollection<RecipeLimit> Limits { get; set; } = new List<RecipeLimit>();
}
