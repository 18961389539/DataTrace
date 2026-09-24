using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class AuditLogger : IAuditLogger
{
    private readonly ConfigDbContext _db;

    public AuditLogger(ConfigDbContext db)
    {
        _db = db;
    }

    public async Task WriteAsync(string userName, string action, string entityType, string? entityKey, string? oldValue, string? newValue, CancellationToken cancellationToken = default)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Time = DateTime.Now,
            UserName = userName,
            Action = action,
            EntityType = entityType,
            EntityKey = entityKey,
            OldValue = oldValue,
            NewValue = newValue
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(IReadOnlyList<AuditLog> Items, int Total)> QueryAsync(
        string? keyword = null,
        string? action = null,
        DateTime? fromInclusive = null,
        int skip = 0,
        int take = 50,
        IReadOnlyList<string>? keywordMatchedActions = null,
        IReadOnlyList<string>? keywordMatchedEntityTypes = null,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0)
        {
            skip = 0;
        }

        if (take < 1)
        {
            take = 50;
        }

        var q = _db.AuditLogs.AsNoTracking().AsQueryable();

        if (fromInclusive is not null)
        {
            q = q.Where(x => x.Time >= fromInclusive.Value);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var actionFilter = action.Trim();
            q = q.Where(x => x.Action == actionFilter);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            var matchedActions = keywordMatchedActions is { Count: > 0 }
                ? keywordMatchedActions.ToList()
                : [];
            var matchedEntities = keywordMatchedEntityTypes is { Count: > 0 }
                ? keywordMatchedEntityTypes.ToList()
                : [];

            q = q.Where(x =>
                x.UserName.Contains(k)
                || x.Action.Contains(k)
                || x.EntityType.Contains(k)
                || (x.EntityKey != null && x.EntityKey.Contains(k))
                || (x.OldValue != null && x.OldValue.Contains(k))
                || (x.NewValue != null && x.NewValue.Contains(k))
                || matchedActions.Contains(x.Action)
                || matchedEntities.Contains(x.EntityType));
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await q
            .OrderByDescending(x => x.Time)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken cancellationToken = default)
    {
        return await _db.AuditLogs.AsNoTracking()
            .Select(x => x.Action)
            .Where(a => a != null && a != "")
            .Distinct()
            .OrderBy(a => a)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
