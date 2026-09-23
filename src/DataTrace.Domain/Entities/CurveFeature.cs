using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;

namespace DataTrace.Domain.Entities;

/// <summary>
/// 曲线特征行：把外置二进制曲线里"算不动"的波形信息字段化，
/// 使其可以参与 SQL 聚合、快照导出，并作为后续 SPC / 异常检测的输入。
/// 每个 CurveRecord 的每条序列一行。
/// </summary>
public class CurveFeature
{
    public long Id { get; set; }
    public long CurveRecordId { get; set; }
    public CurveRecord? CurveRecord { get; set; }

    /// <summary>序列名称，与 CurveSeries.Name 对应。</summary>
    public string SeriesName { get; set; } = "";

    /// <summary>序列角色（X 位移 / Y 压力等）。</summary>
    public SeriesRole Role { get; set; }

    public int PointCount { get; set; }
    public double Min { get; set; }
    public int MinIndex { get; set; }
    public double Peak { get; set; }
    public int PeakIndex { get; set; }
    public double Mean { get; set; }
    public double StdDev { get; set; }
    public double Area { get; set; }
    public double RiseSlope { get; set; }
    public double HoldSlope { get; set; }
    public int RiseIndex { get; set; }
    public int RiseSpan { get; set; }
    public double FallRatio { get; set; }
    public double MaxStep { get; set; }
    public int Oscillations { get; set; }

    // ---- 以下字段是波形基线比对的产出，属于<b>影子模式</b>：只记录、只展示，绝不参与判定 ----
    // 采集时从预先建好的基线缓存里取模板就地打分，因此不需要回查历史。
    // 缓存为空、型号不匹配或基线不可靠时一律留 null，宁可没有数字也不编造。

    /// <summary>与基线的综合偏离分（各维 z 分数的 RMS）。未比对时为 null。</summary>
    public double? DeviationRmsZ { get; set; }

    /// <summary>比对结论。未比对时为 null。</summary>
    public CurveTemplateVerdict? DeviationVerdict { get; set; }

    /// <summary>偏离最大的维度。全维度正常或未比对时为 null。</summary>
    public CurveFeatureDimension? DeviationWorstDimension { get; set; }

    /// <summary>打分所用基线的样本量，便于事后判断这个分数的可信度。</summary>
    public int BaselineSampleCount { get; set; }

    /// <summary>是否有一条比对结论（供界面与查询判断，避免到处写 null 检查）。</summary>
    public bool HasDeviation => DeviationVerdict is not null;
}
