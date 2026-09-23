using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class PlcConnection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public PlcBrand Brand { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6000;
    public int TimeoutMs { get; set; } = 2000;
    public FloatWordOrder FloatWordOrder { get; set; } = FloatWordOrder.CDAB;
    public bool StringHighByteFirst { get; set; } = true;
    public int MergeGapWords { get; set; } = 16;
    public bool Enabled { get; set; } = true;
    public string? Extra { get; set; }

    public ICollection<Station> Stations { get; set; } = new List<Station>();
    public HeartbeatSettings? Heartbeat { get; set; }
}
