using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class ProductRecord
{
    public long Id { get; set; }
    public long CollectRecordId { get; set; }
    public CollectRecord? CollectRecord { get; set; }

    public int PositionIndex { get; set; }
    public bool Occupied { get; set; } = true;
    public Judgement Judgement { get; set; }
    public string? NgReason { get; set; }
}
