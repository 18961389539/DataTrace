using DataTrace.Application.Quality;
using DataTrace.Application.Alarms;
using DataTrace.Application.Runtime;
using DataTrace.Application.Identity;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

public class QualityIncidentStoreTests
{
    [Fact]
    public async Task Incident_workflow_is_idempotent_transactional_and_preserves_record_snapshots()
    {
        await using var context = await InfrastructureContext.CreateAsync();
        var source = Record(12, "20261008-000012", Judgement.Ng);
        var affected = Record(18, "20261008-000018", Judgement.Ok);
        var verification = Record(24, "20261008-000024", Judgement.Ok);
        var collectQuery = new FakeCollectQuery
        {
            Records =
            {
                [("202610", source.Id)] = source,
                [("202610", affected.Id)] = affected,
                [("202610", verification.Id)] = verification
            },
            QueryResult = new CollectQueryResult
            {
                Total = 1,
                Items = [new CollectRecordListItem { MonthKey = "202610", Record = affected }]
            }
        };

        var factory = context.Scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        var store = new QualityIncidentStore(factory, collectQuery, new EmptyUserAdministration());
        var config = context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var qualityAlarm = new AlarmIncident
        {
            Key = "ng:2",
            Kind = (int)LineAlarmKind.ConsecutiveNg,
            Message = "ST020 连续 3 件 NG",
            RaisedAt = new DateTime(2026, 10, 8, 9, 55, 0)
        };
        var systemAlarm = new AlarmIncident
        {
            Key = "disk",
            Kind = (int)LineAlarmKind.DiskLow,
            Message = "数据盘空间不足",
            RaisedAt = new DateTime(2026, 10, 8, 9, 56, 0)
        };
        config.AlarmIncidents.AddRange(qualityAlarm, systemAlarm);
        await config.SaveChangesAsync();
        var operatorActor = new QualityIncidentActor("operator", [AppRoles.Operator]);
        var reviewer = new QualityIncidentActor("engineer", [AppRoles.Engineer]);
        var requestId = Guid.NewGuid();
        var request = new CreateQualityIncidentRequest
        {
            RequestId = requestId,
            MonthKey = "202610",
            RecordId = source.Id,
            Title = "压力超限",
            Description = "连续产品出现超限，待排查",
            Severity = QualityIncidentSeverity.High,
            SourceAlarmId = qualityAlarm.Id,
            ScopeFrom = source.TriggerTime.Date,
            ScopeTo = source.TriggerTime.Date.AddDays(1).AddTicks(-1)
        };

        var id = await store.CreateAsync(request, operatorActor);
        var repeatedId = await store.CreateAsync(request, operatorActor);
        Assert.Equal(id, repeatedId);
        var created = await store.GetAsync(id);
        Assert.Equal(qualityAlarm.Id, created!.Incident.SourceAlarmId);
        Assert.Contains($"关联质量报警 #{qualityAlarm.Id}", created.Activities.Single().Comment);
        await Assert.ThrowsAsync<QualityIncidentOperationException>(() => store.CreateAsync(new CreateQualityIncidentRequest
        {
            RequestId = Guid.NewGuid(),
            MonthKey = "202610",
            RecordId = source.Id,
            Title = "非质量报警关联测试",
            Description = "不应允许",
            SourceAlarmId = systemAlarm.Id
        }, operatorActor));
        await Assert.ThrowsAsync<QualityIncidentOperationException>(() =>
            store.AssignAsync(id, "unknown-user", expectedVersion: 0, reviewer));
        Assert.Equal(0, (await store.GetAsync(id))!.Incident.Version);

        var candidates = await store.SearchAffectedCandidatesAsync(id);
        Assert.Equal(1, candidates.Total);
        Assert.Equal(source.TriggerTime.Date, collectQuery.LastQuery!.From);
        Assert.Equal(source.TriggerTime.Date.AddDays(1).AddTicks(-1), collectQuery.LastQuery.To);
        Assert.Equal(source.StationId, collectQuery.LastQuery.StationId);
        Assert.Equal(source.RecipeCode, collectQuery.LastQuery.RecipeCode);

        await store.LinkEvidenceAsync(id, "202610", affected.Id, QualityEvidencePurpose.Affected, 0, operatorActor);
        var afterAffected = await store.GetAsync(id);
        Assert.Equal(QualityIncidentStatus.InProgress, afterAffected!.Incident.Status);
        Assert.Equal("operator", afterAffected.Incident.Owner);
        Assert.Single(afterAffected.Evidence, item => item.Purpose == QualityEvidencePurpose.Affected);
        await store.LinkEvidenceAsync(id, "202610", affected.Id, QualityEvidencePurpose.Affected, 1, operatorActor);
        await Assert.ThrowsAsync<QualityIncidentOperationException>(() =>
            store.LinkEvidenceAsync(id, "202610", source.Id, QualityEvidencePurpose.Affected, 1, operatorActor));
        Assert.Equal(1, (await store.GetAsync(id))!.Incident.Version);

        await store.AddWorkNoteAsync(id, new QualityIncidentActionCommand
        {
            ActionTaken = "更换压力传感器",
            Comment = "首轮排查发现信号漂移"
        }, expectedVersion: 1, operatorActor);
        await store.SubmitForReviewAsync(id, new QualityIncidentReviewSubmission
        {
            CauseCategory = "设备故障",
            ActionTaken = "更换压力传感器并重新标定",
            VerificationResult = "复测合格",
            MonthKey = "202610",
            RecordId = verification.Id
        }, expectedVersion: 2, operatorActor);
        await store.ReviewAsync(id, new QualityIncidentReviewCommand
        {
            Approved = false,
            Comment = "需补充连续两件验证记录"
        }, expectedVersion: 3, reviewer);

        var returned = await store.GetAsync(id);
        Assert.Equal(QualityIncidentStatus.InProgress, returned!.Incident.Status);
        Assert.Contains(returned.Activities, activity => activity.Kind == QualityIncidentActivityKind.ReviewRejected);

        await store.AddWorkNoteAsync(id, new QualityIncidentActionCommand
        {
            ActionTaken = "连续两件复测，结果均合格"
        }, expectedVersion: 4, operatorActor);
        await store.SubmitForReviewAsync(id, new QualityIncidentReviewSubmission
        {
            CauseCategory = "设备故障",
            ActionTaken = "更换压力传感器并重新标定",
            VerificationResult = "连续两件复测合格",
            MonthKey = "202610",
            RecordId = verification.Id
        }, expectedVersion: 5, operatorActor);
        await store.ReviewAsync(id, new QualityIncidentReviewCommand
        {
            Approved = true,
            Comment = "验证充分，关闭"
        }, expectedVersion: 6, reviewer);

        var closed = await store.GetAsync(id);
        Assert.Equal(QualityIncidentStatus.Closed, closed!.Incident.Status);
        Assert.Equal(7, closed.Incident.Version);
        Assert.Equal(1, closed.Evidence.Count(item => item.Purpose == QualityEvidencePurpose.Source));
        Assert.Equal(1, closed.Evidence.Count(item => item.Purpose == QualityEvidencePurpose.Verification));
        Assert.Contains("LowerLimit", closed.Evidence.Single(item => item.Purpose == QualityEvidencePurpose.Source).TagSnapshotJson);
        Assert.Equal(8, closed.Activities.Count);
        Assert.Equal(8, await context.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>()
            .AuditLogs.CountAsync(log => log.EntityType == "QualityIncident"));
        Assert.Equal(0, (await store.ListAsync(new QualityIncidentQuery())).Total);
        Assert.Equal(1, (await store.ListAsync(new QualityIncidentQuery { Status = QualityIncidentStatus.Closed })).Total);

        await Assert.ThrowsAsync<QualityIncidentConflictException>(() =>
            store.AddWorkNoteAsync(id, new QualityIncidentActionCommand { ActionTaken = "过期版本写入" }, 6, operatorActor));
    }

    private static CollectRecord Record(long id, string serial, Judgement judgement)
        => new()
        {
            Id = id,
            SerialNo = serial,
            PalletCode = $"P{id:D4}",
            StationId = 2,
            StationCode = "ST020",
            RecipeCode = "MODEL-A",
            TriggerTime = new DateTime(2026, 10, 8, 10, 0, (int)(id % 60)),
            CompleteTime = new DateTime(2026, 10, 8, 10, 0, (int)(id % 60)).AddMilliseconds(250),
            Judgement = judgement,
            TagValues =
            [
                new TagValue
                {
                    TagName = "压力",
                    NumericValue = judgement == Judgement.Ng ? 12.5 : 9.8,
                    LowerLimit = 8,
                    UpperLimit = 12,
                    IsOutOfLimit = judgement == Judgement.Ng
                }
            ]
        };

    private sealed class FakeCollectQuery : ICollectQuery
    {
        public Dictionary<(string MonthKey, long RecordId), CollectRecord> Records { get; } = [];
        public CollectQueryResult QueryResult { get; init; } = new() { Total = 0, Items = [] };
        public CollectQueryRequest? LastQuery { get; private set; }

        public Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default)
        {
            LastQuery = request;
            return Task.FromResult(QueryResult);
        }

        public Task<CollectRecord?> GetRecordAsync(string monthKey, long recordId, CancellationToken cancellationToken = default)
            => Task.FromResult(Records.GetValueOrDefault((monthKey, recordId)));

        public Task<bool> ExistsBySerialAsync(string monthKey, string serialNo, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> ExistsCollectAsync(string monthKey, string serialNo, int stationId, DateTime triggerTime, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<long?> FindCollectRecordIdAsync(string monthKey, string serialNo, int stationId, DateTime triggerTime, CancellationToken cancellationToken = default)
            => Task.FromResult<long?>(null);

        public Task<PalletSession?> GetSessionAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<PalletSession?>(null);

        public Task<IReadOnlyList<CollectRecord>> GetSessionRecordsAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CollectRecord>>([]);

        public Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySerialNoAsync(string serialNo, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CollectSessionTrace>>([]);

        public Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySessionIdAsync(long sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CollectSessionTrace>>([]);
    }

    private sealed class EmptyUserAdministration : IUserAdministration
    {
        public Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UserAccount>>([]);

        public Task<IReadOnlyList<UserAccount>> ListAssignableAsync(string actor, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<UserAccount>>([]);

        public Task<UserAdminResult> CreateAsync(string userName, string displayName, string password, string role, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UserAdminResult> UpdateAsync(string userName, string displayName, IReadOnlyList<string> roles, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UserAdminResult> UnlockAsync(string userName, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UserAdminResult> ResetPasswordAsync(string userName, string password, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UserAdminResult> ChangePasswordAsync(string userName, string currentPassword, string newPassword, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UserAdminResult> DeleteAsync(string userName, string actor, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
