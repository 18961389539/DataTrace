using DataTrace.Domain.Enums;

namespace DataTrace.Domain.Entities;

public sealed class QualityIncidentActivity
{
    public long Id { get; set; }
    public long IncidentId { get; set; }
    public QualityIncident Incident { get; set; } = null!;
    public QualityIncidentActivityKind Kind { get; set; }
    public QualityIncidentStatus? FromStatus { get; set; }
    public QualityIncidentStatus? ToStatus { get; set; }
    public string? CauseCategory { get; set; }
    public string? ActionTaken { get; set; }
    public string? VerificationResult { get; set; }
    public string? Comment { get; set; }
    public string Actor { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
