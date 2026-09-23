namespace DataTrace.Domain.Entities;

public class ProductPositionDefinition
{
    public int Id { get; set; }
    public int StationId { get; set; }
    public Station? Station { get; set; }

    /// <summary>固定为 1。托盘只承载一件产品。</summary>
    public int Index { get; set; } = 1;
    public string Name { get; set; } = "产品";

    /// <summary>不再使用。空则视为始终有料。</summary>
    public string? OccupiedAddress { get; set; }
}
