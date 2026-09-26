namespace DataTrace.Domain.Validation;

/// <summary>
/// JSON 字段路径的写法约定。文件源工站用点位现成的「地址」列存字段路径。
/// </summary>
/// <remarks>
/// 只做形状校验，不校验字段是否真的存在 —— 文件由设备一件一覆写，
/// 配错的字段名只会在采集时暴露，界面能做的只是把明显写歪的拦住。
/// </remarks>
public static class JsonFieldRules
{
    /// <summary>字段路径的层数上限，防止把一整串无关的东西填进来。</summary>
    public const int MaxDepth = 8;

    /// <summary>路径长度上限（字符）。</summary>
    public const int MaxLength = 128;

    /// <summary>
    /// 字段路径校验。返回 null 表示可用。
    /// </summary>
    /// <remarks>
    /// 大小写敏感（JSON 属性名本来就大小写敏感），允许 <c>a.b.c</c> 逐层进对象，
    /// 但不支持数组下标：写成 <c>items[0].x</c> 采集时取不到值，
    /// 与其让现场对着"必填点位取空"排查，不如在配置期就说清。
    /// </remarks>
    public static string? Error(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "JSON 字段不能为空";
        }

        var text = path.Trim();
        if (text.Length > MaxLength)
        {
            return $"JSON 字段不能超过 {MaxLength} 个字符";
        }

        if (text.Contains('[') || text.Contains(']'))
        {
            return "JSON 字段不支持数组下标，只能逐层写对象里的字段名（如 force.peak）";
        }

        if (text.StartsWith('.') || text.EndsWith('.'))
        {
            return "JSON 字段的层级分隔符 . 不能出现在开头或结尾";
        }

        var segments = text.Split('.');
        if (segments.Length > MaxDepth)
        {
            return $"JSON 字段最多 {MaxDepth} 层（{MaxDepth} 个用 . 分隔的字段名）";
        }

        if (segments.Any(s => s.Length == 0))
        {
            return "JSON 字段里不能出现连续的两个 .";
        }

        if (segments.Any(s => s.Any(char.IsWhiteSpace)))
        {
            return "JSON 字段名不能包含空白字符";
        }

        return null;
    }
}