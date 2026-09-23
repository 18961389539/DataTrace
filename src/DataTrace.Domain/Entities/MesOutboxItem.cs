using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class MesOutboxItem
{
    public long Id { get; set; }
    public string MonthKey { get; set; } = "";
    public long PalletSessionId { get; set; }
    public string SerialNo { get; set; } = "";
    public string PalletCode { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public MesOutboxStatus Status { get; set; }
    public string PayloadJson { get; set; } = "";
    public string? LastError { get; set; }
}
