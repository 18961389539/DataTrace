using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Validation;

/// <summary>工单档案的字段校验；页面与仓储共用，避免两处口径走样。</summary>
public static class WorkOrderRules
{
    public const int MaxCodeLength = 64;
    public const int MaxRemarkLength = 200;

    public static string? CodeError(string? code)
    {
        var value = (code ?? "").Trim();
        return value.Length == 0 ? "工单号不能为空"
            : value.Length > MaxCodeLength ? $"工单号不能超过 {MaxCodeLength} 个字符"
            : null;
    }

    public static string? PlannedQuantityError(int? plannedQuantity)
        => plannedQuantity is <= 0 ? "计划数量必须大于 0；留空表示未下达数量" : null;

    public static string? DateRangeError(DateTime? start, DateTime? end)
        => start is { } s && end is { } e && e.Date < s.Date ? "结束日期不能早于开始日期" : null;

    public static string? StatusError(WorkOrderStatus status)
        => Enum.IsDefined(status) ? null : "工单状态无效";

    public static string? RemarkError(string? remark)
        => (remark ?? "").Trim().Length > MaxRemarkLength ? $"备注不能超过 {MaxRemarkLength} 个字符" : null;
}
