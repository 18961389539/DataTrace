namespace DataTrace.Domain.Validation;

/// <summary>
/// 型号编码的格式约束（页面与仓储共用一份，规则只有这一处）。
/// </summary>
public static class RecipeCodeRules
{
    /// <summary>校验型号编码；返回 null 表示通过，否则返回可以直接给工程师看的那句话。</summary>
    /// <remarks>
    /// 放行的字符只有字母、数字、连字符与下划线，逗号必须排除：
    /// 型号的历史编码（<see cref="Entities.Recipe.PreviousCodes"/>）是逗号分隔串，
    /// 编码自带逗号会让"改码后历史样本仍算本型号"这套口径被拆成好几个不存在的编码，
    /// 曲线基线就会把别的型号的样本拉进本型号。
    /// </remarks>
    public static string? Error(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "型号编码不能为空";
        }

        var trimmed = code.Trim();
        return trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? null
            : $"型号编码「{trimmed}」只能包含字母、数字、连字符和下划线（不能含逗号或空格）";
    }
}
