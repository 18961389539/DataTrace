using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

/// <summary>工站自定义设备报警条目（如电机过热）。</summary>
public class StationAlarmDefinition
{
    public int Id { get; set; }
    public int StationId { get; set; }
    public Station? Station { get; set; }

    /// <summary>同一工站内唯一的名称。</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>关联点位；有值时按最近一次采集数值与限值比较。</summary>
    public int? TagDefinitionId { get; set; }
    public TagDefinition? TagDefinition { get; set; }

    public StationAlarmCompare Compare { get; set; } = StationAlarmCompare.Above;

    /// <summary>阈值；关联点位时必填。</summary>
    public double? Limit { get; set; }

    /// <summary>连续监视采样周期（毫秒）；当前仅配置预留，判定随报警后台约 5 秒汇总。</summary>
    public int SampleIntervalMs { get; set; } = 2000;

    public string? Remark { get; set; }

    /// <summary>浅拷贝（含 Id）。编辑对话框传克隆体，取消时不污染跟踪实体。</summary>
    public StationAlarmDefinition Clone() => (StationAlarmDefinition)MemberwiseClone();
}