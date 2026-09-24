namespace DataTrace.Web.Components.Shared;

/// <summary>
/// 「产品型号」下拉的取值：<see cref="Recipe"/> 是筛选状态（null = 全部型号、空串 = 未选型号、
/// 其它 = 精确匹配编码），<see cref="Text"/> 是选项文案。查询页与报表页共用一套取值语义。
/// </summary>
/// <remarks>
/// 为什么取值要自带文案，而不是直接用 "*" / "c:CODE" 这类 string 哨兵值：
/// MudSelect 解析"选项文案"发生在 OnAfterRender，而静态预渲染不跑 OnAfterRender，
/// 那一帧它退化成取值自身的 ToString()。取值是 string 时，页面一打开会先闪一下哨兵值
/// （* / c:A100）再变成"全部型号"；让 ToString() 就是文案，两帧画的是同一个字符串。
/// 顺带把哨兵撞名也钉死了：取值是 record，相等性比的是两个字段，
/// 型号编码哪怕自己就叫 "*" 或 "__EMPTY__"，也不等于哨兵。
/// </remarks>
public sealed record RecipeFilterValue(string? Recipe, string Text)
{
    /// <summary>全部型号（不按型号过滤）。</summary>
    public static readonly RecipeFilterValue All = new(null, "全部型号");

    /// <summary>未选型号：记录里 RecipeCode 为空串的那些。</summary>
    public static readonly RecipeFilterValue NoRecipe = new("", "未选型号");

    /// <summary>下拉里显示的就是文案，预渲染与交互后一致。</summary>
    public override string ToString() => Text;
}
