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

    public ICollection<ProductPositionDefinition> Positions { get; set; } = new List<ProductPositionDefinition>();
    public ICollection<TagDefinition> Tags { get; set; } = new List<TagDefinition>();
    public ICollection<CurveDefinition> Curves { get; set; } = new List<CurveDefinition>();
}
