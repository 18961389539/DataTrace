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

    /// <summary>首站确定的型号编码。后续工站用这一套，不再跟着中途换型走。空串表示当时没选型号。</summary>
    public string RecipeCode { get; set; } = "";

    /// <summary>这件目前停在的工站。首站写入后，每过一站更新。</summary>
    public int LastStationId { get; set; }

    public string LastStationCode { get; set; } = "";

    public DateTime? LastActivityAt { get; set; }
}
