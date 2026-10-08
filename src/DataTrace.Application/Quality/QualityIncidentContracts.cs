using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Application.Runtime;

namespace DataTrace.Application.Quality;

public sealed record QualityIncidentActor(string UserName, IReadOnlyCollection<string> Roles)
{
    public bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

public interface IQualityIncidentActorProvider
{
    Task<QualityIncidentActor> GetCurrentAsync(CancellationToken cancellationToken = default);
}

public sealed class QualityIncidentQuery
{
    public QualityIncidentStatus? Status { get; init; }
    public int? StationId { get; init; }
    public string? Owner { get; init; }
    public string? Keyword { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 25;
}

public sealed class QualityIncidentListItem
{
    public long Id { get; init; }
    public required string IncidentNumber { get; init; }
    public required string Title { get; init; }
    public required string StationCode { get; init; }
    public required string RecipeCode { get; init; }
    public QualityIncidentSeverity Severity { get; init; }
    public QualityIncidentStatus Status { get; init; }
    public string? Owner { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public int Version { get; init; }
    public int EvidenceCount { get; init; }
}

public sealed class QualityIncidentListResult
{
    public required IReadOnlyList<QualityIncidentListItem> Items { get; init; }
    public required int Total { get; init; }
}

public sealed class QualityIncidentDetails
{
    public required QualityIncident Incident { get; init; }
    public required IReadOnlyList<QualityIncidentEvidence> Evidence { get; init; }
    public required IReadOnlyList<QualityIncidentActivity> Activities { get; init; }
}

public sealed class CreateQualityIncidentRequest
{
    public required Guid RequestId { get; init; }
    public required string MonthKey { get; init; }
    public long RecordId { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public QualityIncidentSeverity Severity { get; init; } = QualityIncidentSeverity.Medium;
    public int? StationId { get; init; }
    public string? StationCode { get; init; }
    public string? RecipeCode { get; init; }
    public long? SourceAlarmId { get; init; }
    public DateTime? ScopeFrom { get; init; }
    public DateTime? ScopeTo { get; init; }
}

public sealed class QualityIncidentActionCommand
{
    public required string ActionTaken { get; init; }
    public string? Comment { get; init; }
}

public sealed class QualityIncidentReviewSubmission
{
    public required string CauseCategory { get; init; }
    public required string ActionTaken { get; init; }
    public required string VerificationResult { get; init; }
    public required string MonthKey { get; init; }
    public long RecordId { get; init; }
}

public sealed class QualityIncidentReviewCommand
{
    public bool Approved { get; init; }
    public required string Comment { get; init; }
}

public class QualityIncidentOperationException(string message) : InvalidOperationException(message);

public sealed class QualityIncidentConflictException(string message) : QualityIncidentOperationException(message);

public interface IQualityIncidentService
{
    Task<QualityIncidentListResult> ListAsync(QualityIncidentQuery query, CancellationToken cancellationToken = default);
    Task<QualityIncidentDetails?> GetAsync(long id, CancellationToken cancellationToken = default);
    Task<CollectQueryResult> SearchAffectedCandidatesAsync(long id, int skip = 0, int take = 50, CancellationToken cancellationToken = default);
    Task<long> CreateAsync(CreateQualityIncidentRequest request, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task TakeAsync(long id, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task AssignAsync(long id, string owner, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task LinkEvidenceAsync(long id, string monthKey, long recordId, QualityEvidencePurpose purpose, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task AddWorkNoteAsync(long id, QualityIncidentActionCommand command, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task SubmitForReviewAsync(long id, QualityIncidentReviewSubmission command, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task ReviewAsync(long id, QualityIncidentReviewCommand command, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
    Task ReopenAsync(long id, string reason, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default);
}
