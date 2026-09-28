namespace DataTrace.Domain.Entities;

/// <summary>
/// 一次异常从出现到有人接手、条件解除的记录。保存在配置库，重启后还在。
/// </summary>
public class AlarmIncident
{
    public long Id { get; set; }

    /// <summary>同一次异常持续期间不变，例如 heartbeat、mes、ng:工站号。</summary>
    public string Key { get; set; } = "";

    public int Kind { get; set; }

    public string Message { get; set; } = "";

    public DateTime RaisedAt { get; set; }

    public DateTime? AcknowledgedAt { get; set; }

    public string? AcknowledgedBy { get; set; }

    public DateTime? ClearedAt { get; set; }
}
