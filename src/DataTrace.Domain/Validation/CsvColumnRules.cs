namespace DataTrace.Domain.Validation;

/// <summary>CSV 列名写在点位的「地址」上。只校验形状，不查文件里有没有这一列。</summary>
public static class CsvColumnRules
{
    public const int MaxLength = 128;

    public static string? Error(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "CSV 列名不能为空";
        }

        var text = name.Trim();
        if (text.Length > MaxLength)
        {
            return $"CSV 列名不能超过 {MaxLength} 个字符";
        }

        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return "CSV 列名不能包含逗号、引号或换行";
        }

        return null;
    }
}
