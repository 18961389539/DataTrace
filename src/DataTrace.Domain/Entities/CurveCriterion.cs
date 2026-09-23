namespace DataTrace.Domain.Entities;

/// <summary>
/// 曲线级判据：对波形特征（峰值、面积、上升/保压段斜率、回落比例…）设定上下限。
/// 全部字段可空，null 表示"该项不判"，因此未配置判据的曲线行为与改造前完全一致。
/// </summary>
/// <remarks>
/// 与 TagDefinition 的限值互补：点位限值只看瞬时值，曲线判据看整段波形，
/// 能拦住"值在限内但压力保持不住 / 位移爬升异常"这类漏检。
/// </remarks>
public class CurveCriterion
{
    public int Id { get; set; }
    public int CurveDefinitionId { get; set; }
    public CurveDefinition? CurveDefinition { get; set; }

    /// <summary>
    /// 目标序列名。留空表示作用于该曲线的主序列（优先 Y 角色，否则第一条）。
    /// </summary>
    public string SeriesName { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>峰值下限。</summary>
    public double? PeakMin { get; set; }

    /// <summary>峰值上限。</summary>
    public double? PeakMax { get; set; }

    /// <summary>均值下限。</summary>
    public double? MeanMin { get; set; }

    /// <summary>均值上限。</summary>
    public double? MeanMax { get; set; }

    /// <summary>积分面积下限。</summary>
    public double? AreaMin { get; set; }

    /// <summary>积分面积上限。</summary>
    public double? AreaMax { get; set; }

    /// <summary>上升段斜率下限（过小说明爬升无力）。</summary>
    public double? RiseSlopeMin { get; set; }

    /// <summary>上升段斜率上限（过大说明冲击）。</summary>
    public double? RiseSlopeMax { get; set; }

    /// <summary>保压段斜率下限（负得太多说明保持不住）。</summary>
    public double? HoldSlopeMin { get; set; }

    /// <summary>保压段斜率上限（正得太多说明未卸压）。</summary>
    public double? HoldSlopeMax { get; set; }

    /// <summary>回落比例上限。</summary>
    public double? FallRatioMax { get; set; }

    /// <summary>相邻采样点最大跳变上限。</summary>
    public double? MaxStepMax { get; set; }

    /// <summary>波动标准差上限。</summary>
    public double? StdDevMax { get; set; }

    /// <summary>振荡次数上限。</summary>
    public int? OscillationMax { get; set; }
}
