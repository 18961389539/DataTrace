namespace DataTrace.Domain.Enums;

public enum QualityIncidentStatus
{
    Open,
    InProgress,
    PendingReview,
    Closed
}

public enum QualityIncidentSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum QualityEvidencePurpose
{
    Source,
    Affected,
    Verification
}

public enum QualityIncidentActivityKind
{
    Created,
    Assigned,
    ActionRecorded,
    EvidenceLinked,
    SubmittedForReview,
    ReviewRejected,
    Closed,
    Reopened
}
