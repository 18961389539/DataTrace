using System.ComponentModel.DataAnnotations.Schema;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;

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

    /// <summary>
    /// 该点位的判异规则开关（按位，见 <see cref="Evaluation.SpcRuleMask"/>）。null = 全套规则。
    /// </summary>
    /// <remarks>
    /// 有些点位天然带周期性（往复动作的位移之类），交替/趋势规则会长期误报；
    /// 按点位关掉误报的那一条，比整个点位停用更精确 —— 停用会把超限这类真问题一起丢掉。
    /// </remarks>
    public int? SpcRuleMask { get; set; }

    /// <summary>冻结控制限的中心线；与上下限一起构成一套冻结基线。</summary>
    public double? ControlCenterLine { get; set; }

    /// <summary>冻结控制限的上限（UCL）。</summary>
    public double? ControlUpperLimit { get; set; }

    /// <summary>冻结控制限的下限（LCL）。</summary>
    public double? ControlLowerLimit { get; set; }

    /// <summary>冻结这套控制限时用到的样本数。</summary>
    public int? ControlSampleCount { get; set; }

    /// <summary>冻结时间。</summary>
    public DateTime? ControlCapturedAt { get; set; }

    /// <summary>冻结人（登录名）。体系审核问"这条控制限是谁定的"时得有答案。</summary>
    public string? ControlCapturedBy { get; set; }

    /// <summary>
    /// 点位冻结的控制限；三个关键值缺一即视为没冻结（老库补列后就是全空）。
    /// </summary>
    /// <remarks>计算属性，不进库：库里存的是上面三个数值列与来源信息。</remarks>
    [NotMapped]
    public FrozenControlLimits? FrozenControlLimits
        => ControlCenterLine is { } centerLine
           && ControlUpperLimit is { } upper
           && ControlLowerLimit is { } lower
            ? new FrozenControlLimits(
                centerLine,
                upper,
                lower,
                ControlSampleCount ?? 0,
                ControlCapturedAt ?? default,
                ControlCapturedBy ?? "")
            : null;

    /// <summary>
    /// 逐字段浅拷贝（含 <c>Id</c>）。配置快照是共享只读实例，改前必须克隆。
    /// </summary>
    /// <remarks>
    /// 用 MemberwiseClone 而不是手写字段清单：手写版本每加一个字段就要记得补一行，
    /// 漏掉的那次会在保存（整行 SetValues）时把新字段无声清空 —— 已经栽过一次。
    /// </remarks>
    public TagDefinition Clone() => (TagDefinition)MemberwiseClone();
}
