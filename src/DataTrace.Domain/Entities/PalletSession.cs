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

    /// <summary>
    /// 会话判定为 NG 时，第一条触发 NG 的工站编码（按触发时刻，同一时刻再按记录 Id）。
    /// 判定本身只说"任一站 NG 即 NG"，事后区分不了"首站就废"和"末站才废" ——
    /// 首因就是回答"从哪一站、哪一刻开始出问题"。判定不为 NG 时为 null。
    /// </summary>
    public string? FirstNgStationCode { get; set; }

    /// <summary>首条 NG 记录的触发时刻，与 <see cref="FirstNgStationCode"/> 一起定位那一条。</summary>
    public DateTime? FirstNgAt { get; set; }

    /// <summary>首条 NG 记录的结果码：区分质量判废、数据校验失败与流程异常（跳站）。</summary>
    public short? FirstNgResultCode { get; set; }

    /// <summary>首因说明：首条 NG 记录的采集错误与各不良品位的判定原因去重拼成的一句。</summary>
    public string? FirstNgReason { get; set; }

    public ICollection<CollectRecord> Records { get; set; } = new List<CollectRecord>();
}
