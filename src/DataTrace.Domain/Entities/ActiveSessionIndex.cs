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

    /// <summary>
    /// 首站冻结的型号参数快照（JSON：编码 + 限值覆盖行）。
    /// 只冻结编码的话，在制期间改限值、改名、停用都会让后续工站判错规则或找不到型号；
    /// 快照让整件产品从头到尾用同一套限值判定，记录因此可以重建当时的判定依据。
    /// 旧会话没有快照时按编码回落当前配置（兼容升级前已开的会话）。
    /// </summary>
    public string? RecipeSnapshot { get; set; }

    /// <summary>这件目前停在的工站。首站写入后，每过一站更新。</summary>
    public int LastStationId { get; set; }

    public string LastStationCode { get; set; } = "";

    public DateTime? LastActivityAt { get; set; }
}
