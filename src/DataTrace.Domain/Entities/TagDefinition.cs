using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class TagDefinition
{
    public int Id { get; set; }
    public int StationId { get; set; }
    public Station? Station { get; set; }

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public PlcDataType DataType { get; set; }
    public int Length { get; set; }
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string? Unit { get; set; }

    /// <summary>规格下限（红线）。超出即判废。</summary>
    public double? LowerLimit { get; set; }

    /// <summary>规格上限（红线）。超出即判废。</summary>
    public double? UpperLimit { get; set; }

    /// <summary>
    /// 预警下限（黄线）。应不严于 <see cref="LowerLimit"/>；落在预警带只提示，不判废。
    /// </summary>
    public double? WarningLowerLimit { get; set; }

    /// <summary>
    /// 预警上限（黄线）。应不宽于 <see cref="UpperLimit"/>；落在预警带只提示，不判废。
    /// </summary>
    public double? WarningUpperLimit { get; set; }

    /// <summary>目标值。仅用于报表与偏移分析，不参与判定。</summary>
    public double? TargetValue { get; set; }

    public bool IsRequired { get; set; } = true;

    /// <summary>0 = 工站级公共点位；1 = 托盘上的唯一产品。</summary>
    public int PositionIndex { get; set; }
    public bool Enabled { get; set; } = true;
}
