namespace DataTrace.Domain.Entities;

public class CurveRecord
{
    public long Id { get; set; }
    public long CollectRecordId { get; set; }
    public CollectRecord? CollectRecord { get; set; }

    public int CurveDefinitionId { get; set; }
    public string CurveCode { get; set; } = "";
    public string CurveName { get; set; } = "";
    public int PositionIndex { get; set; }
    public int PointCount { get; set; }
    public string RelativePath { get; set; } = "";
    public long FileSize { get; set; }
    public uint Crc32 { get; set; }

    /// <summary>该曲线各序列的特征行，随曲线记录一起落库，便于 SQL 层聚合波形指标。</summary>
    public ICollection<CurveFeature> Features { get; set; } = new List<CurveFeature>();
}
