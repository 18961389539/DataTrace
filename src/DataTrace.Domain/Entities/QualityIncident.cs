using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public sealed class QualityIncident
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public string IncidentNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public QualityIncidentSeverity Severity { get; set; }
    public QualityIncidentStatus Status { get; set; }
    public int? StationId { get; set; }
    public string StationCode { get; set; } = "";
    public string RecipeCode { get; set; } = "";
    public long? SourceAlarmId { get; set; }
    public DateTime? ScopeFrom { get; set; }
    public DateTime? ScopeTo { get; set; }
    public string CreatedBy { get; set; } = "";
    public string? Owner { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Version { get; set; }
    public ICollection<QualityIncidentEvidence> Evidence { get; set; } = new List<QualityIncidentEvidence>();
    public ICollection<QualityIncidentActivity> Activities { get; set; } = new List<QualityIncidentActivity>();
}