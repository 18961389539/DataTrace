using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class AlarmIncidentStore : IAlarmIncidents
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly IAuditLogger _audit;

    public AlarmIncidentStore(IDbContextFactory<ConfigDbContext> factory, IAuditLogger audit)
    {
        _factory = factory;
        _audit = audit;
    }

    public async Task<IReadOnlyList<AlarmIncidentDraft>> ListAttentionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await Attention(db, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlarmIncidentDraft>> ListRecentClosedAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AlarmIncidents.AsNoTracking()
            .Where(item => item.AcknowledgedAt != null && item.ClearedAt != null)
            .OrderByDescending(item => item.ClearedAt)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToDraft).ToList();
    }

    public async Task RaiseAsync(LineAlarm alarm, DateTime now, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var open = await db.AlarmIncidents
                .Where(item => item.Key == alarm.Key && (item.AcknowledgedAt == null || item.ClearedAt == null))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var current = open
                .Where(item => item.AcknowledgedAt == null)
                .OrderByDescending(item => item.RaisedAt)
                .ThenByDescending(item => item.Id)
                .FirstOrDefault();
            if (current is null)
            {
                db.AlarmIncidents.Add(new AlarmIncident
                {
                    Key = alarm.Key,
                    Kind = (int)alarm.Kind,
                    Message = alarm.Message,
                    RaisedAt = now
                });
            }
            else
            {
                current.Kind = (int)alarm.Kind;
                current.Message = alarm.Message;
                current.ClearedAt = null;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<IReadOnlyList<AlarmIncidentDraft>> ApplyAsync(
        IReadOnlyList<LineAlarm> active,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var rows = await db.AlarmIncidents
                .Where(item => item.AcknowledgedAt == null || item.ClearedAt == null)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var merged = AlarmIncidentRules.Merge(rows.Select(ToDraft).ToList(), active, now);
            var kept = merged.Select(item => item.Id).Where(id => id > 0).ToHashSet();
            var byId = rows.ToDictionary(item => item.Id);

            foreach (var row in rows)
            {
                if (!kept.Contains(row.Id))
                {
                    db.AlarmIncidents.Remove(row);
                }
            }

            foreach (var draft in merged)
            {
                if (draft.Id == 0)
                {
                    db.AlarmIncidents.Add(ToEntity(draft));
                    continue;
                }

                if (!byId.TryGetValue(draft.Id, out var row))
                {
                    continue;
                }

                row.Kind = (int)draft.Kind;
                row.Message = draft.Message;
                row.ClearedAt = draft.ClearedAt;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return await Attention(db, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task<AlarmAckResult> AcknowledgeAsync(long id, string userName, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await db.AlarmIncidents.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                return new AlarmAckResult { Found = false };
            }

            if (row.AcknowledgedAt is not null)
            {
                return new AlarmAckResult
                {
                    Found = true,
                    AlreadyTaken = true,
                    TakenBy = row.AcknowledgedBy
                };
            }

            var who = string.IsNullOrWhiteSpace(userName) ? "（未知）" : userName.Trim();
            row.AcknowledgedAt = DateTime.Now;
            row.AcknowledgedBy = who;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await _audit.WriteAsync(
                        who,
                        "Acknowledge",
                        "Alarm",
                        row.Key,
                        null,
                        $"接手：{row.Message}",
                        cancellationToken)
                    .ConfigureAwait(false);
                return new AlarmAckResult { Found = true };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new AlarmAckResult { Found = true, AuditError = ex.Message };
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<IReadOnlyList<AlarmIncidentDraft>> Attention(ConfigDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.AlarmIncidents.AsNoTracking()
            .Where(item => item.AcknowledgedAt == null || item.ClearedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToDraft)
            .OrderBy(item => item.NeedsOwner ? 0 : 1)
            .ThenBy(item => item.RaisedAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static AlarmIncidentDraft ToDraft(AlarmIncident row)
        => new()
        {
            Id = row.Id,
            Key = row.Key,
            Kind = (LineAlarmKind)row.Kind,
            Message = row.Message,
            RaisedAt = row.RaisedAt,
            AcknowledgedAt = row.AcknowledgedAt,
            AcknowledgedBy = row.AcknowledgedBy,
            ClearedAt = row.ClearedAt
        };

    private static AlarmIncident ToEntity(AlarmIncidentDraft draft)
        => new()
        {
            Key = draft.Key,
            Kind = (int)draft.Kind,
            Message = draft.Message,
            RaisedAt = draft.RaisedAt,
            AcknowledgedAt = draft.AcknowledgedAt,
            AcknowledgedBy = draft.AcknowledgedBy,
            ClearedAt = draft.ClearedAt
        };
}
