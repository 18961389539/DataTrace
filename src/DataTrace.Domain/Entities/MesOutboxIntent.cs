namespace DataTrace.Domain.Entities;

public sealed class MesOutboxIntent
{
    public long Id { get; set; }
    public string EventId { get; set; } = "";
    public string MonthKey { get; set; } = "";
    public long PalletSessionId { get; set; }
    public string SerialNo { get; set; } = "";
    public string PalletCode { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string PayloadJson { get; set; } = "";
}