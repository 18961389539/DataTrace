using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public class HeartbeatSettings
{
    public int Id { get; set; }
    public int PlcConnectionId { get; set; }
    public PlcConnection? PlcConnection { get; set; }

    public string Address { get; set; } = "";
    public int IntervalMs { get; set; } = 1000;
    public HeartbeatMode Mode { get; set; } = HeartbeatMode.Increment;
    public bool Enabled { get; set; } = true;
}
