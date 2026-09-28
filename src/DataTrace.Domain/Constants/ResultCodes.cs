namespace DataTrace.Domain.Constants;

/// <summary>
/// 与 PLC 触发寄存器共用的握手码。1 为触发。
/// 2 是采集完成且质量放行；11 是采集完成但质量不合格、不放行；3–10 只表示采集失败。
/// </summary>
public static class ResultCodes
{
    public const short Trigger = 1;

    /// <summary>采集完成，并且判定不是不合格。PLC 据此放行。</summary>
    public const short Success = 2;
    public const short PlcReadFailed = 3;
    public const short InvalidPalletCode = 4;
    public const short DataValidationFailed = 5;
    public const short DatabaseWriteFailed = 6;
    public const short InternalError = 7;
    public const short ProcessAbnormal = 8;

    /// <summary>文件源工站读不到或读不懂 JSON 文件（含字段路径取不到值之外的整文件故障）。</summary>
    public const short FileSourceFailed = 9;

    /// <summary>原始 JSON 归档失败。按约定归档失败即采集失败：设备可以重发这一件。</summary>
    public const short ArchiveFailed = 10;

    /// <summary>
    /// 采集完成，但判定为不合格。记录照常留下，PLC 不放行。
    /// 不占用 3–10：那些码表示这一件没有采成。
    /// </summary>
    public const short QualityRejected = 11;

    public static string Describe(short code) => code switch
    {
        Trigger => "触发待采集",
        Success => "采集成功",
        PlcReadFailed => "PLC 读取失败",
        InvalidPalletCode => "托盘码非法",
        DataValidationFailed => "数据校验失败",
        DatabaseWriteFailed => "数据库写入失败",
        InternalError => "系统内部异常",
        ProcessAbnormal => "流程异常（跳站等）",
        FileSourceFailed => "数据文件读取失败",
        ArchiveFailed => "原始数据归档失败",
        QualityRejected => "质量不合格",
        _ => $"未知码 {code}"
    };
}
