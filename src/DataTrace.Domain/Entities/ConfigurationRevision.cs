namespace DataTrace.Domain.Entities;

/// <summary>Immutable, complete configuration state committed together with its audit record.</summary>
public sealed class ConfigurationRevision
{
    public long Id { get; set; }
    public int ConfigVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public string UserName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityKey { get; set; }
    public string ValidationResult { get; set; } = "Passed";
    public long? ParentRevisionId { get; set; }
    public string SnapshotJson { get; set; } = "{}";
    public string DiffJson { get; set; } = "[]";
}
