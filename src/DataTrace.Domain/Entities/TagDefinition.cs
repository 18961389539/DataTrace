using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class TagDefinition
{
    public int Id { get; set; }
    public int StationId { get; set; }
    public Station? Station { get; set; }

    /// <summary>同一工站内唯一的名称。采集、限值与趋势按数字主键关联，名称只负责给人看。</summary>
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

    /// <summary>固定为 1：点位属于托盘上的这一件。保存时由仓储写成 1。</summary>
    public int PositionIndex { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 该点位的取值来源：PLC 寄存器，或本工站那一个 JSON 文件里的字段。
    /// </summary>
    /// <remarks>
    /// 按点位而不是按工站选：一个工站同时有"PLC 报的保压时间"和"智能传感器导出的力值"是常态。
    /// 文件路径仍然是工站级的（一台设备每件覆写一个文件），所以文件源点位都读
    /// <see cref="Station.DataFilePath"/> 指向的那一份；选 JSON 时
    /// <see cref="Address"/> 按文件里的字段名解释。
    /// </remarks>
    public TagDataSource Source { get; set; } = TagDataSource.Plc;
}
