namespace DataTrace.Domain.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public DateTime Time { get; set; }
    public string UserName { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityKey { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
