namespace DataTrace.Domain.Entities;

public class CurveDefinition
{
    public int Id { get; set; }
    public int StationId { get; set; }
    public Station? Station { get; set; }

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int PointCount { get; set; }
    public int PositionIndex { get; set; }
    public bool Enabled { get; set; } = true;

    public ICollection<CurveSeries> Series { get; set; } = new List<CurveSeries>();

    /// <summary>波形判据。为空表示该曲线不做波形判定，行为与仅有点位限值时一致。</summary>
    public ICollection<CurveCriterion> Criteria { get; set; } = new List<CurveCriterion>();
}
