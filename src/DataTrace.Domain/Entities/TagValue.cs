using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class TagValue
{
    public long Id { get; set; }
    public long CollectRecordId { get; set; }
    public CollectRecord? CollectRecord { get; set; }

    public int TagId { get; set; }
    public string TagName { get; set; } = "";
    public int PositionIndex { get; set; }
    public PlcDataType DataType { get; set; }
    public double? NumericValue { get; set; }
    public string? TextValue { get; set; }

    /// <summary>是否超出规格限。只有它为 true 才判废。</summary>
    public bool IsOutOfLimit { get; set; }

    /// <summary>是否落在预警带（黄区）。预警不判废，只用于早期提示与趋势统计。</summary>
    public bool IsWarning { get; set; }

    /// <summary>
    /// 判定这一刻实际生效的四道限值（点位默认值叠加当前型号覆盖后的结果）。
    /// 与 <see cref="CollectRecord.RecipeCode"/> 同一思路：不记下来，事后就无法解释
    /// "当时为什么判废"——限值随时会被工程师改掉。
    /// 历史月份库里的老记录这四列是 null，界面必须区分"没配限值"和"那会儿还没记"。
    /// </summary>
    public double? LowerLimit { get; set; }

    public double? UpperLimit { get; set; }
    public double? WarningLowerLimit { get; set; }
    public double? WarningUpperLimit { get; set; }
}
