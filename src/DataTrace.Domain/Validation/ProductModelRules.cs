namespace DataTrace.Domain.Validation;

/// <summary>业务型号字段的硬性限制；产品型号档案与工单快照共用。</summary>
public static class ProductModelRules
{
    public const int MaxLength = 64;

    public static string? Error(string? model)
        => (model ?? "").Trim().Length > MaxLength ? $"型号不能超过 {MaxLength} 个字符" : null;
}
