namespace DataTrace.Domain.Enums;

/// <summary>工单状态。枚举值直接落库，改动即破坏兼容。</summary>
public enum WorkOrderStatus
{
    Pending = 0,
    InProgress = 1,
    Completed = 2,
    Cancelled = 3
}
