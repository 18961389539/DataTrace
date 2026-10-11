namespace DataTrace.Domain.Entities;

/// <summary>
/// 月库里的"在制索引更新意图"：月库事务提交成功后、config.db 的 ActiveSessions
/// 更新完成前，进程硬退出（断电/被杀）会留下一个没有补偿的窗口 —— 记录已提交，
/// 在制索引却缺失或过期。把索引更新写成本表的一条意图、与业务数据同事务落库，
/// 重启后由对账器重放，窗口就只剩"索引晚一点追上"，而不是"索引永久错位"。
/// </summary>
public sealed class SessionIndexIntent
{
    public long Id { get; set; }

    public string PalletCode { get; set; } = "";

    /// <summary>这条意图对应的流水号。重放时若 config.db 里的在制会话已被更新的流水接管，本条作废。</summary>
    public string SerialNo { get; set; } = "";

    public string MonthKey { get; set; } = "";

    /// <summary>0 = 更新索引（Upsert），1 = 移除索引（会话关闭）。</summary>
    public int Operation { get; set; }

    /// <summary>Upsert 时的 ActiveSessionIndex 全字段 JSON（含首站冻结的型号快照）。</summary>
    public string PayloadJson { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>重放成功的时间；null 表示还未转运。</summary>
    public DateTime? AppliedAt { get; set; }
}
