namespace DataTrace.Domain.Entities;

/// <summary>配置库中的在制会话索引，用于跨月定位运行库。</summary>
public class ActiveSessionIndex
{
    public int Id { get; set; }
    public string PalletCode { get; set; } = "";
    public string SerialNo { get; set; } = "";
    public long SessionId { get; set; }
    public string MonthKey { get; set; } = "";
    public DateTime StartTime { get; set; }
}
