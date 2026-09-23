using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class PalletSession
{
    public long Id { get; set; }
    public string SerialNo { get; set; } = "";
    public string PalletCode { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Open;
    public Judgement Judgement { get; set; } = Judgement.None;

    public ICollection<CollectRecord> Records { get; set; } = new List<CollectRecord>();
}
