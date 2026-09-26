using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class CollectRecord
{
    public long Id { get; set; }
    public long PalletSessionId { get; set; }
    public PalletSession? PalletSession { get; set; }

    public string SerialNo { get; set; } = "";
    public string PalletCode { get; set; } = "";
    public int StationId { get; set; }
    public string StationCode { get; set; } = "";
    public DateTime TriggerTime { get; set; }
    public DateTime CompleteTime { get; set; }
    public int DurationMs { get; set; }
    public short ResultCode { get; set; }
    public Judgement Judgement { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 本次判定所用的产品型号编码；空串表示按点位默认限值判定。
    /// 限值会随型号切换而变，不记下来事后就无法解释"当时为什么判废"。
    /// </summary>
    public string RecipeCode { get; set; } = "";

    /// <summary>
    /// 本次采集读到的原始 JSON 文件的归档相对路径。空串表示该工站点位不来自文件。
    /// 设备每件覆写同一个文件，归档是唯一能事后回看"设备当时到底写了什么"的东西。
    /// </summary>
    public string ArchivePath { get; set; } = "";

    public long ArchiveFileSize { get; set; }

    /// <summary>归档文件原始字节的 CRC32，读取时校验损坏（与曲线文件同一套做法）。</summary>
    public uint ArchiveCrc32 { get; set; }

    public ICollection<ProductRecord> Products { get; set; } = new List<ProductRecord>();
    public ICollection<TagValue> TagValues { get; set; } = new List<TagValue>();
    public ICollection<CurveRecord> Curves { get; set; } = new List<CurveRecord>();
}
