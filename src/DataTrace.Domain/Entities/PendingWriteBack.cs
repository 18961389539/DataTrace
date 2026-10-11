namespace DataTrace.Domain.Entities;

/// <summary>
/// "已提交、待回写确认"的采集事件留证。回写失败只改显示状态的话，触发寄存器仍是触发值，
/// 下一轮扫描会把它当成新事件重新采集 —— 首站把旧会话标异常新建流水，其他站新增重复记录。
/// 有了这条留证，触发命中时先查本表：有未确认件就只重试确认（重写响应码 + 读回），
/// 绝不重新走一遍采集。重启后留证仍在月库里，同样生效。
/// </summary>
public sealed class PendingWriteBack
{
    public long Id { get; set; }

    /// <summary>Exact runtime record identity; null only for legacy pending rows without a record pointer.</summary>
    public long? CollectRecordId { get; set; }

    public string PalletCode { get; set; } = "";

    public string SerialNo { get; set; } = "";

    public int StationId { get; set; }

    public string StationCode { get; set; } = "";

    public string MonthKey { get; set; } = "";

    public long PalletSessionId { get; set; }

    /// <summary>当时判定出的响应码。确认重试写回的就是这个值，不重新判定。</summary>
    public short ResultCode { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>确认重试成功的时间；null 表示还未确认。</summary>
    public DateTime? ConfirmedAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>下次允许重试的时刻：连续失败按指数退避，别把重试打成对 PLC 的报文风暴。</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>True when device event identity is unavailable or the event cannot be proven safe to confirm.</summary>
    public bool ManualReviewRequired { get; set; }

    public DateTime? ManualReviewRequiredAt { get; set; }

    public string? ManualReviewReason { get; set; }

    /// <summary>Explicit operator resolution; the pending row is retained as an audit fact.</summary>
    public DateTime? ManualReviewResolvedAt { get; set; }

    public string? ManualReviewResolvedBy { get; set; }

    public string? ManualReviewResolution { get; set; }

    public string? ManualReviewDeviceStateEvidence { get; set; }

    public int? PlcConnectionId { get; set; }

    public string? TriggerAddress { get; set; }

    public short? TriggerValue { get; set; }

    public string? ConnectionFingerprint { get; set; }
}
