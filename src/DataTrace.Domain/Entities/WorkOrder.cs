using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

/// <summary>
/// 生产工单档案：计划与责任信息，不参与采集和判定，也不下发 PLC。
/// </summary>
/// <remarks>
/// 采集链路仍按“当前型号 + 托盘会话”运行；工单只是台账，因此不进配置快照与修订历史，
/// 删除工单也不影响已采集的历史记录。
/// </remarks>
public class WorkOrder
{
    public int Id { get; set; }

    /// <summary>工单号，全局唯一（忽略大小写）。</summary>
    public string Code { get; set; } = "";

    /// <summary>产品型号；留空表示暂未指定，删除型号时置空而不是连带删工单。</summary>
    public int? RecipeId { get; set; }
    public Recipe? Recipe { get; set; }

    /// <summary>
    /// 型号快照：创建工单时从所选型号带出，之后可改；型号改名不会回写这里。
    /// </summary>
    public string? Model { get; set; }

    /// <summary>计划数量；留空表示未下达数量。</summary>
    public int? PlannedQuantity { get; set; }

    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Pending;

    /// <summary>
    /// 是否为当前工单。全系统最多一张；设为当前同时把状态置为生产中，
    /// 状态改离生产中时由仓储自动取消。
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>
    /// 设为当前的时刻：看板据此估算已完成件数（此后的走出末站件数，按型号过滤）。
    /// 取消当前或改离生产中时清空；只是估算口径，不是精确的工单产量。
    /// </summary>
    public DateTime? CurrentSince { get; set; }

    /// <summary>计划开始/结束日期（只取日期部分）。</summary>
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }

    public string? Remark { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
