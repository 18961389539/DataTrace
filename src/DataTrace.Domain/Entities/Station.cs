using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class Station
{
    public int Id { get; set; }
    public int PlcConnectionId { get; set; }
    public PlcConnection? PlcConnection { get; set; }

    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Sequence { get; set; }
    public bool IsFirstStation { get; set; }
    public bool IsLastStation { get; set; }

    /// <summary>触发与响应共用的 Int16 字地址。</summary>
    public string TriggerAddress { get; set; } = "";
    public short TriggerValue { get; set; } = 1;

    public string PalletCodeAddress { get; set; } = "";
    public int PalletCodeLength { get; set; } = 16;
    public PlcDataType PalletCodeDataType { get; set; } = PlcDataType.String;

    /// <summary>托盘固定承载 1 件产品。保留字段以兼容已有配置库。</summary>
    public int PositionCount { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 文件源点位读取的数据文件路径（JSON 或 CSV，见 <see cref="DataFileFormat"/>）。设备每件覆写同一个文件、写完才置触发位，
    /// 因此不需要等文件，也不需要按件区分文件 —— 每次触发无条件重读。
    /// </summary>
    /// <remarks>
    /// 路径留在工站上而不是每个点位各填一份：一台设备（一个工站）写一个文件，
    /// 逐点位重复填路径只会让它们慢慢不一致。
    /// </remarks>
    public string DataFilePath { get; set; } = "";

    /// <summary>数据文件按 JSON 字段还是 CSV 列名解析。默认 JSON。</summary>
    public DataFileFormat DataFileFormat { get; set; } = DataFileFormat.Json;

    public ICollection<ProductPositionDefinition> Positions { get; set; } = new List<ProductPositionDefinition>();
    public ICollection<TagDefinition> Tags { get; set; } = new List<TagDefinition>();
    public ICollection<CurveDefinition> Curves { get; set; } = new List<CurveDefinition>();
}
