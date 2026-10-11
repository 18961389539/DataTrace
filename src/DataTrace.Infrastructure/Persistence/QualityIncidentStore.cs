using System.Globalization;
using System.Text.Json;
using DataTrace.Application.Alarms;
using DataTrace.Application.Identity;
using DataTrace.Application.Quality;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class QualityIncidentStore : IQualityIncidentService
{
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly ICollectQuery _collectQuery;
    private readonly IUserAdministration _users;

    public QualityIncidentStore(IDbContextFactory<ConfigDbContext> factory, ICollectQuery collectQuery, IUserAdministration users)
    {
        _factory = factory;
        _collectQuery = collectQuery;
        _users = users;
    }

    public async Task<QualityIncidentListResult> ListAsync(QualityIncidentQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Skip < 0 || query.Take is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "分页范围无效。");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var incidents = db.QualityIncidents.AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            incidents = incidents.Where(item => item.Status == status);
        }
        else
        {
            incidents = incidents.Where(item => item.Status != QualityIncidentStatus.Closed);
        }

        if (query.StationId is { } stationId)
        {
            incidents = incidents.Where(item => item.StationId == stationId);
        }

        if (!string.IsNullOrWhiteSpace(query.Owner))
        {
            var owner = query.Owner.Trim();
            incidents = incidents.Where(item => item.Owner != null && item.Owner.Contains(owner));
        }

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            incidents = incidents.Where(item => item.IncidentNumber.Contains(keyword)
                                                 || item.Title.Contains(keyword)
                                                 || item.StationCode.Contains(keyword)
                                                 || item.RecipeCode.Contains(keyword)
                                                 || (item.Owner != null && item.Owner.Contains(keyword)));
        }

        var total = await incidents.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await incidents
            .OrderBy(item => item.Status == QualityIncidentStatus.Closed)
            .ThenByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(item => new QualityIncidentListItem
            {
                Id = item.Id,
                IncidentNumber = item.IncidentNumber,
                Title = item.Title,
                StationCode = item.StationCode,
                RecipeCode = item.RecipeCode,
                Severity = item.Severity,
                Status = item.Status,
                Owner = item.Owner,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                Version = item.Version,
                EvidenceCount = item.Evidence.Count
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new QualityIncidentListResult { Items = items, Total = total };
    }

    public async Task<QualityIncidentDetails?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var incident = await db.QualityIncidents.AsNoTracking()
            .Include(item => item.Evidence)
            .Include(item => item.Activities)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (incident is null)
        {
            return null;
        }

        return new QualityIncidentDetails
        {
            Incident = incident,
            Evidence = incident.Evidence.OrderBy(item => item.TriggerTime).ThenBy(item => item.Id).ToList(),
            Activities = incident.Activities.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToList()
        };
    }

    public async Task<CollectQueryResult> SearchAffectedCandidatesAsync(long id, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        if (skip < 0 || take is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(take), "潜在影响查询分页范围无效。");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var incident = await db.QualityIncidents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new QualityIncidentOperationException("质量异常单不存在。");
        if (incident.ScopeFrom is not { } from || incident.ScopeTo is not { } to)
        {
            return new CollectQueryResult { Total = 0, Items = [] };
        }

        return await _collectQuery.QueryAsync(new CollectQueryRequest
        {
            From = from,
            To = to,
            StationId = incident.StationId,
            RecipeCode = incident.RecipeCode,
            Skip = skip,
            Take = take,
            SortBy = CollectSortField.TriggerTime,
            SortDescending = true
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CreateAsync(CreateQualityIncidentRequest request, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanWork(actor);
        if (request.RequestId == Guid.Empty)
        {
            throw new QualityIncidentOperationException("建单请求标识无效，请重新提交。");
        }

        var title = QualityIncidentRules.RequiredText(request.Title, "异常标题", 120);
        var description = QualityIncidentRules.RequiredText(request.Description, "异常描述");
        ValidateScope(request.ScopeFrom, request.ScopeTo);
        ValidateMonthKey(request.MonthKey);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.QualityIncidents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Id;
        }

        AlarmIncident? sourceAlarm = null;
        if (request.SourceAlarmId is { } alarmId)
        {
            sourceAlarm = await db.AlarmIncidents.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == alarmId, cancellationToken)
                .ConfigureAwait(false);
            if (sourceAlarm is null || (LineAlarmKind)sourceAlarm.Kind != LineAlarmKind.ConsecutiveNg)
            {
                throw new QualityIncidentOperationException("关联的报警不存在或不是连续 NG 质量报警。");
            }
        }

        var source = await RequireRecordAsync(request.MonthKey, request.RecordId, cancellationToken).ConfigureAwait(false);
        var now = DateTime.Now;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        existing = await db.QualityIncidents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.RequestId == request.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        var incident = new QualityIncident
        {
            RequestId = request.RequestId,
            IncidentNumber = $"pending-{Guid.NewGuid():N}",
            Title = title,
            Description = description,
            Severity = request.Severity,
            Status = QualityIncidentStatus.Open,
            StationId = source.StationId,
            StationCode = source.StationCode,
            RecipeCode = source.RecipeCode,
            ScopeFrom = request.ScopeFrom,
            ScopeTo = request.ScopeTo,
            SourceAlarmId = request.SourceAlarmId,
            CreatedBy = RequiredActor(actor),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.QualityIncidents.Add(incident);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        incident.IncidentNumber = $"QI-{now:yyyyMMdd}-{incident.Id:D6}";
        db.QualityIncidentEvidence.Add(ToEvidence(incident.Id, request.MonthKey, source, QualityEvidencePurpose.Source));
        db.QualityIncidentActivities.Add(new QualityIncidentActivity
        {
            IncidentId = incident.Id,
            Kind = QualityIncidentActivityKind.Created,
            ToStatus = QualityIncidentStatus.Open,
            Comment = sourceAlarm is null
                ? description
                : $"{description}\n关联质量报警 #{sourceAlarm.Id} · {sourceAlarm.Message} · {sourceAlarm.RaisedAt:yyyy-MM-dd HH:mm:ss}",
            Actor = RequiredActor(actor),
            CreatedAt = now
        });
        db.AuditLogs.Add(ToAuditLog(incident, db.QualityIncidentActivities.Local.Single(), null));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return incident.Id;
    }

    public Task TakeAsync(long id, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanWork(actor);
        var owner = RequiredActor(actor);
        return MutateAsync(id, expectedVersion, actor, (_, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.Open, QualityIncidentStatus.InProgress);
            if (!string.IsNullOrWhiteSpace(incident.Owner)
                && !string.Equals(incident.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && !CanManage(actor))
            {
                throw new QualityIncidentOperationException($"异常单已由 {incident.Owner} 负责。");
            }

            if (incident.Status == QualityIncidentStatus.InProgress
                && string.Equals(incident.Owner, owner, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var from = incident.Status;
            incident.Owner = owner;
            incident.Status = QualityIncidentStatus.InProgress;
            return Activity(incident, QualityIncidentActivityKind.Assigned, actor, now,
                from, incident.Status, comment: "接手处理");
        }, cancellationToken);
    }

    public async Task AssignAsync(long id, string owner, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanManage(actor);
        var assignedTo = QualityIncidentRules.RequiredText(owner, "负责人", 100);
        var target = (await _users.ListAssignableAsync(actor.UserName, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(user => string.Equals(user.UserName, assignedTo, StringComparison.OrdinalIgnoreCase));
        if (target is null || target.IsLocked
            || !target.Roles.Any(role => role == AppRoles.Administrator || role == AppRoles.Engineer || role == AppRoles.Operator))
        {
            throw new QualityIncidentOperationException("负责人必须是未锁定的操作员、工程师或管理员账号。");
        }

        await MutateAsync(id, expectedVersion, actor, (db, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.Open, QualityIncidentStatus.InProgress);
            var previousOwner = incident.Owner;
            incident.Owner = assignedTo;
            return Activity(incident, QualityIncidentActivityKind.Assigned, actor, now,
                from: incident.Status, to: incident.Status,
                comment: previousOwner is null ? $"指派给 {assignedTo}" : $"负责人由 {previousOwner} 改为 {assignedTo}");
            }, cancellationToken).ConfigureAwait(false);
    }

    public async Task LinkEvidenceAsync(long id, string monthKey, long recordId, QualityEvidencePurpose purpose, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanWork(actor);
        if (purpose != QualityEvidencePurpose.Affected)
        {
            throw new QualityIncidentOperationException("此入口只用于登记潜在受影响记录；源记录和验证记录由对应流程添加。");
        }

        ValidateMonthKey(monthKey);
        var record = await RequireRecordAsync(monthKey, recordId, cancellationToken).ConfigureAwait(false);
        await MutateAsync(id, expectedVersion, actor, (db, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.Open, QualityIncidentStatus.InProgress);
            var from = incident.Status;
            EnsureWorkOwner(incident, actor);
            if (incident.Status == QualityIncidentStatus.Open)
            {
                incident.Status = QualityIncidentStatus.InProgress;
            }

            var existing = db.QualityIncidentEvidence.Local.FirstOrDefault(item => item.IncidentId == id
                                                                                    && item.MonthKey == monthKey
                                                                                    && item.RecordId == recordId)
                           ?? db.QualityIncidentEvidence.FirstOrDefault(item => item.IncidentId == id
                                                                                && item.MonthKey == monthKey
                                                                                && item.RecordId == recordId);
            if (existing is not null)
            {
                if (existing.Purpose != purpose)
                {
                    throw new QualityIncidentOperationException(
                        $"此记录已登记为“{EvidencePurposeText(existing.Purpose)}”，不能重复归类为“{EvidencePurposeText(purpose)}”。");
                }

                return null;
            }

            db.QualityIncidentEvidence.Add(ToEvidence(id, monthKey, record, purpose));
            return Activity(incident, QualityIncidentActivityKind.EvidenceLinked, actor, now,
                from, incident.Status,
                comment: $"登记潜在受影响记录 {record.SerialNo} · {record.StationCode}");
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task AddWorkNoteAsync(long id, QualityIncidentActionCommand command, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanWork(actor);
        var action = QualityIncidentRules.RequiredText(command.ActionTaken, "处置措施");
        var comment = OptionalText(command.Comment, 2000);
        return MutateAsync(id, expectedVersion, actor, (_, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.Open, QualityIncidentStatus.InProgress);
            EnsureWorkOwner(incident, actor);
            var from = incident.Status;
            incident.Status = QualityIncidentStatus.InProgress;
            return Activity(incident, QualityIncidentActivityKind.ActionRecorded, actor, now,
                from, incident.Status, action: action, comment: comment);
        }, cancellationToken);
    }

    public async Task SubmitForReviewAsync(long id, QualityIncidentReviewSubmission command, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanWork(actor);
        var cause = QualityIncidentRules.RequiredText(command.CauseCategory, "原因分类", 100);
        var action = QualityIncidentRules.RequiredText(command.ActionTaken, "处置措施");
        var verification = QualityIncidentRules.RequiredText(command.VerificationResult, "验证结论");
        ValidateMonthKey(command.MonthKey);
        var verificationRecord = await RequireRecordAsync(command.MonthKey, command.RecordId, cancellationToken).ConfigureAwait(false);

        await MutateAsync(id, expectedVersion, actor, (db, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.InProgress);
            EnsureWorkOwner(incident, actor);
            var existing = db.QualityIncidentEvidence.Local.FirstOrDefault(item => item.IncidentId == id
                                                                                    && item.MonthKey == command.MonthKey
                                                                                    && item.RecordId == command.RecordId)
                           ?? db.QualityIncidentEvidence.FirstOrDefault(item => item.IncidentId == id
                                                                                && item.MonthKey == command.MonthKey
                                                                                && item.RecordId == command.RecordId);
            if (existing is not null && existing.Purpose != QualityEvidencePurpose.Verification)
            {
                throw new QualityIncidentOperationException("验证记录必须是尚未关联到此异常单的独立采集记录。");
            }

            if (existing is null)
            {
                db.QualityIncidentEvidence.Add(ToEvidence(id, command.MonthKey, verificationRecord,
                    QualityEvidencePurpose.Verification));
            }

            var from = incident.Status;
            incident.Status = QualityIncidentStatus.PendingReview;
            return Activity(incident, QualityIncidentActivityKind.SubmittedForReview, actor, now,
                from, incident.Status, cause, action, verification);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task ReviewAsync(long id, QualityIncidentReviewCommand command, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanManage(actor);
        var comment = QualityIncidentRules.RequiredText(command.Comment, command.Approved ? "复核结论" : "退回原因");
        return MutateAsync(id, expectedVersion, actor, (_, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.PendingReview);
            var from = incident.Status;
            if (command.Approved)
            {
                incident.Status = QualityIncidentStatus.Closed;
            }
            else
            {
                incident.Status = QualityIncidentStatus.InProgress;
            }

            return Activity(incident,
                command.Approved ? QualityIncidentActivityKind.Closed : QualityIncidentActivityKind.ReviewRejected,
                actor, now, from, incident.Status, comment: comment);
        }, cancellationToken);
    }

    public Task ReopenAsync(long id, string reason, int expectedVersion, QualityIncidentActor actor, CancellationToken cancellationToken = default)
    {
        QualityIncidentRules.RequireCanManage(actor);
        var explanation = QualityIncidentRules.RequiredText(reason, "重新打开原因");
        return MutateAsync(id, expectedVersion, actor, (_, incident, now) =>
        {
            EnsureStatus(incident, QualityIncidentStatus.Closed);
            var from = incident.Status;
            incident.Status = QualityIncidentStatus.InProgress;
            return Activity(incident, QualityIncidentActivityKind.Reopened, actor, now,
                from, incident.Status, comment: explanation);
        }, cancellationToken);
    }

    private async Task MutateAsync(
        long id,
        int expectedVersion,
        QualityIncidentActor actor,
        Func<ConfigDbContext, QualityIncident, DateTime, QualityIncidentActivity?> mutate,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var incident = await db.QualityIncidents.FirstOrDefaultAsync(item => item.Id == id, cancellationToken).ConfigureAwait(false)
                       ?? throw new QualityIncidentOperationException("质量异常单不存在。");
        if (incident.Version != expectedVersion)
        {
            throw new QualityIncidentConflictException("异常单已被其他人修改，请刷新后重试。");
        }

        var now = DateTime.Now;
        var activity = mutate(db, incident, now);
        if (activity is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        incident.Version++;
        incident.UpdatedAt = now;
        db.QualityIncidentActivities.Add(activity);
        db.AuditLogs.Add(ToAuditLog(incident, activity, activity.FromStatus));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new QualityIncidentConflictException("异常单已被其他人修改，请刷新后重试。");
        }
    }

    private async Task<CollectRecord> RequireRecordAsync(string monthKey, long recordId, CancellationToken cancellationToken)
        => await _collectQuery.GetRecordAsync(monthKey, recordId, cancellationToken).ConfigureAwait(false)
           ?? throw new QualityIncidentOperationException("关联的采集记录不存在或已按保留策略清理。");

    private static QualityIncidentEvidence ToEvidence(long incidentId, string monthKey, CollectRecord record,
        QualityEvidencePurpose purpose)
        => new()
        {
            IncidentId = incidentId,
            MonthKey = monthKey,
            RecordId = record.Id,
            Purpose = purpose,
            SerialNo = record.SerialNo,
            PalletCode = record.PalletCode,
            StationCode = record.StationCode,
            RecipeCode = record.RecipeCode,
            TriggerTime = record.TriggerTime,
            Judgement = record.Judgement,
            ResultCode = record.ResultCode,
            TagSnapshotJson = JsonSerializer.Serialize(record.TagValues.Select(value => new
            {
                value.TagName,
                value.NumericValue,
                value.TextValue,
                value.LowerLimit,
                value.UpperLimit,
                value.IsOutOfLimit,
                value.IsWarning
            }))
        };

    private static QualityIncidentActivity Activity(QualityIncident incident, QualityIncidentActivityKind kind,
        QualityIncidentActor actor, DateTime now, QualityIncidentStatus? from = null, QualityIncidentStatus? to = null,
        string? cause = null, string? action = null, string? verification = null, string? comment = null)
        => new()
        {
            IncidentId = incident.Id,
            Kind = kind,
            FromStatus = from,
            ToStatus = to,
            CauseCategory = cause,
            ActionTaken = action,
            VerificationResult = verification,
            Comment = comment,
            Actor = RequiredActor(actor),
            CreatedAt = now
        };

    private static AuditLog ToAuditLog(QualityIncident incident, QualityIncidentActivity activity, QualityIncidentStatus? previousStatus)
        => new()
        {
            Time = activity.CreatedAt,
            UserName = activity.Actor,
            Action = activity.Kind == QualityIncidentActivityKind.Created ? "Create" : "Update",
            EntityType = "QualityIncident",
            EntityKey = incident.IncidentNumber,
            OldValue = previousStatus?.ToString(),
            NewValue = JsonSerializer.Serialize(new
            {
                activity.Kind,
                Status = incident.Status,
                incident.Owner,
                activity.CauseCategory,
                activity.ActionTaken,
                activity.VerificationResult,
                activity.Comment
            }),
            Outcome = "Success",
            Source = "Blazor Server UI",
            CorrelationId = Guid.NewGuid().ToString("N")
        };

    private static string RequiredActor(QualityIncidentActor actor)
        => QualityIncidentRules.RequiredText(actor.UserName, "当前用户", 100);

    private static string? OptionalText(string? text, int maxLength)
    {
        var result = text?.Trim();
        if (result?.Length > maxLength)
        {
            throw new QualityIncidentOperationException($"备注不能超过 {maxLength} 个字符。");
        }

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static void ValidateScope(DateTime? from, DateTime? to)
    {
        if (from.HasValue != to.HasValue)
        {
            throw new QualityIncidentOperationException("排查范围必须同时填写起止时间。");
        }

        if (from > to)
        {
            throw new QualityIncidentOperationException("排查范围的开始时间不能晚于结束时间。");
        }
    }

    private static void ValidateMonthKey(string monthKey)
    {
        if (!DateTime.TryParseExact(monthKey, "yyyyMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new QualityIncidentOperationException("采集记录月份无效。");
        }
    }

    private static bool CanManage(QualityIncidentActor actor)
    {
        return actor.IsInRole(AppRoles.Administrator) || actor.IsInRole(AppRoles.Engineer);
    }

    private static string EvidencePurposeText(QualityEvidencePurpose purpose) => purpose switch
    {
        QualityEvidencePurpose.Source => "源证据",
        QualityEvidencePurpose.Affected => "潜在影响",
        QualityEvidencePurpose.Verification => "验证证据",
        _ => "未知用途"
    };

    private static void EnsureWorkOwner(QualityIncident incident, QualityIncidentActor actor)
    {
        var actorName = RequiredActor(actor);
        if (!string.IsNullOrWhiteSpace(incident.Owner)
            && !string.Equals(incident.Owner, actorName, StringComparison.OrdinalIgnoreCase)
            && !CanManage(actor))
        {
            throw new QualityIncidentOperationException($"异常单当前由 {incident.Owner} 负责。");
        }

        incident.Owner ??= actorName;
    }

    private static void EnsureStatus(QualityIncident incident, params QualityIncidentStatus[] allowed)
    {
        if (!allowed.Contains(incident.Status))
        {
            throw new QualityIncidentOperationException("当前状态不允许此操作，请刷新后重试。");
        }
    }
}
