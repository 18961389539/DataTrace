namespace DataTrace.Mcp.Models;

public sealed record TagValueDto(
    int TagId,
    string Name,
    string? Unit,
    double? Value,
    string? Text,
    bool OutOfLimit,
    bool Warning,
    double? LowerLimit,
    double? UpperLimit,
    double? WarningLowerLimit,
    double? WarningUpperLimit);

public sealed record CurveSummaryDto(string Code, string Name, int PointCount, long FileSize);

public sealed record RecordDto(
    string MonthKey,
    long Id,
    long SessionId,
    string SerialNo,
    string PalletCode,
    int StationId,
    string StationCode,
    DateTime TriggerTime,
    DateTime CompleteTime,
    int DurationMs,
    short ResultCode,
    string Judgement,
    string RecipeCode,
    string? ErrorMessage,
    IReadOnlyList<TagValueDto>? TagValues,
    IReadOnlyList<CurveSummaryDto>? Curves);

public sealed record RecordPageDto(int Total, IReadOnlyList<RecordDto> Items, string? NextCursor);

public sealed record SessionDto(
    string MonthKey,
    long Id,
    string SerialNo,
    string PalletCode,
    DateTime StartTime,
    DateTime? EndTime,
    string Status,
    string Judgement,
    string? FirstNgStationCode,
    DateTime? FirstNgAt,
    short? FirstNgResultCode,
    string? FirstNgReason,
    string RecipeCode,
    int RecordCount);

public sealed record SessionPageDto(int Total, IReadOnlyList<SessionDto> Items);

public sealed record StationDto(
    int Id,
    string Code,
    string Name,
    int Sequence,
    bool Enabled,
    bool IsFirstStation,
    bool IsLastStation,
    int TagCount,
    int CurveCount);

public sealed record TagDto(
    int Id,
    int StationId,
    string Name,
    string? Unit,
    string Purpose,
    double? LowerLimit,
    double? UpperLimit,
    double? WarningLowerLimit,
    double? WarningUpperLimit,
    double? TargetValue,
    bool IsRequired,
    bool Enabled,
    string Source);

public sealed record RecipeDto(
    int Id,
    string Code,
    string Name,
    string? Model,
    bool Enabled,
    int LimitCount);

public sealed record RecipeLimitDto(
    int TagId,
    double? LowerLimit,
    double? UpperLimit,
    double? WarningLowerLimit,
    double? WarningUpperLimit,
    double? TargetValue);

public sealed record StationCatalogEntryDto(StationDto Station, IReadOnlyList<TagDto> Tags);

public sealed record RecipeCatalogEntryDto(RecipeDto Recipe, IReadOnlyList<RecipeLimitDto> Limits);

public sealed record MesOutboxStatusDto(
    int PendingCount,
    DateTime? OldestPendingAt,
    DateTime? LastAttemptAt,
    bool? LastAttemptSucceeded,
    string? LastError,
    DateTime? LastSuccessAt);

public sealed record CollectorLoopTickDto(
    DateTime At,
    int ConfiguredIntervalMs,
    long WorkMs,
    long ScanMs,
    long HeartbeatMs,
    int PlcCount,
    int TriggeredCount,
    bool Overran);

public sealed record CollectorLoopDto(
    int Capacity,
    long AverageWorkMs,
    long MaxWorkMs,
    int OverrunCount,
    IReadOnlyList<CollectorLoopTickDto> RecentTicks);

public sealed record StationLatencyDto(string StationCode, int SampleCount, double P95Ms, double P99Ms, DateTime LastSampleAt);

public sealed record ActiveCollectionDto(string StationCode, DateTime StartedAt, long ElapsedMs);

public sealed record PlcFailureDto(DateTime At, string Address, long DurationMs, string? Error);

public sealed record PlcTrafficSummaryDto(
    int PlcConnectionId,
    string Name,
    int TotalCount,
    int FailureCount,
    long LastDurationMs,
    long MaxDurationMs,
    DateTime? LastSuccessAt,
    IReadOnlyList<PlcFailureDto> RecentFailures);

public sealed record CollectionDiagnosticsDto(
    int CollectionMaxAgeSeconds,
    CollectorLoopDto Loop,
    IReadOnlyList<StationLatencyDto> Latencies,
    IReadOnlyList<ActiveCollectionDto> ActiveCollections,
    IReadOnlyList<ActiveCollectionDto> OverdueCollections,
    IReadOnlyList<PlcTrafficSummaryDto> PlcTraffic);

public sealed record PlcSummaryDto(int Id, string Name);

public sealed record OperationalConfigDto(
    string ActiveRecipeCode,
    string? ActiveRecipeName,
    bool CollectEnabled,
    bool MesEnabled,
    bool SimulatorAutoRun,
    int ShiftStartHour,
    int ShiftLengthHours,
    int RetentionYears,
    IReadOnlyList<RecipeDto> Recipes,
    IReadOnlyList<PlcSummaryDto> Plcs);

public sealed record WorkOrderDto(
    int Id,
    string Code,
    string? Model,
    int? PlannedQuantity,
    string Status,
    bool IsCurrent,
    DateTime? CurrentSince,
    DateTime? StartDate,
    DateTime? EndDate,
    string? Remark);

public sealed record AlarmDto(
    long Id,
    string Key,
    string Kind,
    string Message,
    DateTime RaisedAt,
    DateTime? AcknowledgedAt,
    string? AcknowledgedBy,
    DateTime? ClearedAt,
    bool NeedsOwner,
    bool StillActive);

public sealed record QualityIncidentSummaryDto(
    long Id,
    string IncidentNumber,
    string Title,
    string StationCode,
    string RecipeCode,
    string Severity,
    string Status,
    string? Owner,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Version,
    int EvidenceCount);

public sealed record QualityIncidentPageDto(int Total, IReadOnlyList<QualityIncidentSummaryDto> Items);

public sealed record QualityIncidentEvidenceDto(
    long RecordId,
    string MonthKey,
    string Purpose,
    string SerialNo,
    string PalletCode,
    string StationCode,
    string RecipeCode,
    DateTime TriggerTime,
    string Judgement,
    short ResultCode);

public sealed record QualityIncidentActivityDto(
    string Kind,
    string? FromStatus,
    string? ToStatus,
    string? CauseCategory,
    string? ActionTaken,
    string? VerificationResult,
    string? Comment,
    string Actor,
    DateTime CreatedAt);

public sealed record QualityIncidentDetailsDto(
    long Id,
    string IncidentNumber,
    string Title,
    string Description,
    string Severity,
    string Status,
    string StationCode,
    string RecipeCode,
    string? Owner,
    string CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int Version,
    IReadOnlyList<QualityIncidentEvidenceDto> Evidence,
    IReadOnlyList<QualityIncidentActivityDto> Activities);

public sealed record AuditLogDto(
    long Id,
    DateTime Time,
    string UserName,
    string Action,
    string EntityType,
    string? EntityKey,
    string Outcome,
    string Source,
    string? SourceIp,
    string? CorrelationId);

public sealed record AuditPageDto(int Total, IReadOnlyList<AuditLogDto> Items);

public sealed record AlarmAckDto(int Taken, int AlreadyTaken, int Missing, string? AuditError);

public sealed record QualityIncidentMutationDto(long Id, int Version, string Status);

public sealed record DailyThroughputDto(
    DateTime Day,
    string ShiftLabel,
    int Total,
    int Ok,
    int Ng,
    int QualityNg,
    int CollectNg,
    int None,
    double FirstPassYield);

public sealed record RecipeThroughputDto(
    string RecipeCode,
    int Total,
    int Ok,
    int Ng,
    int Pending,
    double PassRate);

public sealed record ThroughputReportDto(
    IReadOnlyList<DailyThroughputDto> ByDay,
    IReadOnlyList<RecipeThroughputDto> ByRecipe);

public sealed record PieceYieldDragDto(
    string StationCode,
    string PointName,
    int PieceCount,
    int HighCount,
    int LowCount);

public sealed record PieceFirstNgDto(string StationCode, int PieceCount);

public sealed record SpecClearanceDto(double Nearest, int NearCount, bool HasBand, string Text);

public sealed record PieceYieldReportDto(
    IReadOnlyList<DailyThroughputDto> ByShift,
    IReadOnlyList<RecipeThroughputDto> ByRecipe,
    PieceYieldDragDto? Drag,
    PieceFirstNgDto? FirstNg,
    SpecClearanceDto? Clearance);

public sealed record ShiftStopDto(
    DateTime ShiftStart,
    bool HadPasses,
    bool WholeLine,
    string? StationCode,
    int DurationMinutes,
    int LostPieces,
    int StopCount,
    int TotalLostPieces,
    string Detail,
    string Brief);

public sealed record IssueTopItemDto(string Name, int Count);

public sealed record IssueTopDto(
    IReadOnlyList<IssueTopItemDto> Items,
    int Total,
    IReadOnlyList<IssueTopItemDto> MissingItems,
    int MissingTotal);

public sealed record ReportResultDto(
    string Kind,
    ThroughputReportDto? Throughput,
    PieceYieldReportDto? PieceYield,
    IReadOnlyList<ShiftStopDto>? ShiftStops,
    IssueTopDto? Top);

public sealed record TrendPointDto(
    DateTime Time,
    double Value,
    string PalletCode,
    double? LowerLimit,
    double? UpperLimit);

public sealed record TrendResultDto(
    int TagId,
    DateTime From,
    DateTime To,
    int Total,
    bool Downsampled,
    IReadOnlyList<TrendPointDto> Points);

public sealed record SpcSummaryDto(
    int Count,
    double Mean,
    double Min,
    double Max,
    double OverallStdDev,
    double WithinStdDev,
    double? Cp,
    double? Cpk,
    double? Pp,
    double? Ppk,
    double CenterLine,
    double UpperControlLimit,
    double LowerControlLimit,
    bool ControlLimitsFrozen,
    string Verdict,
    string? Note);

public sealed record SpcViolationDto(string Rule, string Description, int StartIndex, int EndIndex);

public sealed record SpcSegmentDto(
    int Number,
    DateTime StartTime,
    DateTime EndTime,
    double? LowerLimit,
    double? UpperLimit,
    bool LimitsFromConfig,
    int Count,
    SpcSummaryDto Summary,
    IReadOnlyList<SpcViolationDto> Violations);

public sealed record ProcessCapabilityDto(
    int TagId,
    string TagName,
    string? Unit,
    string RecipeCode,
    double? TargetValue,
    string Verdict,
    bool SpecChangedInWindow,
    IReadOnlyList<SpcSegmentDto> Segments,
    IReadOnlyList<SpcViolationDto> Violations);

public sealed record LiveTagDto(
    string Name,
    string Display,
    string? Unit,
    double? Value,
    string? Text,
    bool OutOfLimit,
    bool Warning);

public sealed record LiveCurveDto(string Name, string? Judgement, string? Reason);

public sealed record StationStatusDto(
    int StationId,
    string Code,
    string Name,
    int Sequence,
    string State,
    string? LastPalletCode,
    string? LastSerialNo,
    short? LastResultCode,
    string LastJudgement,
    DateTime? LastCompleteTime,
    double? LastPieceGapSeconds,
    int? LastDurationMs,
    string? LastError,
    bool LastWriteBackOk,
    int LastWriteBackAttempts,
    IReadOnlyList<LiveTagDto> Tags,
    IReadOnlyList<LiveCurveDto> Curves);

public sealed record PlcStatusDto(int Id, string Name, bool Connected, string? LastError);

public sealed record CollectFeedItemDto(
    string MonthKey,
    long RecordId,
    string StationCode,
    string PalletCode,
    string SerialNo,
    string RecipeCode,
    short ResultCode,
    string Judgement,
    DateTime CompleteTime,
    int DurationMs,
    string? Error);

public sealed record NgStreakDto(int StationId, string StationCode, int Count, string Kind);

public sealed record LineStatusDto(
    string? ActiveRecipeCode,
    string? ActiveRecipeName,
    DateTime LastCollectorAt,
    bool Ready,
    string? ReadinessReason,
    IReadOnlyList<StationStatusDto> Stations,
    IReadOnlyList<PlcStatusDto> Plcs,
    IReadOnlyList<CollectFeedItemDto> Recent,
    IReadOnlyList<NgStreakDto> NgStreaks);
