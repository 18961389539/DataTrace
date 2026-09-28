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

    public async Task WriteAsync(
        string userName,
        string action,
        string entityType,
        string? entityKey,
        string? oldValue,
        string? newValue,
        CancellationToken cancellationToken = default,
        string outcome = "Success",
        string source = "Blazor Server UI",
        string? sourceIp = null,
        string? correlationId = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            Time = DateTime.Now,
            UserName = userName,
            Action = action,
            EntityType = entityType,
            EntityKey = entityKey,
            OldValue = oldValue,
            NewValue = newValue,
            Outcome = outcome,
            Source = source,
            SourceIp = sourceIp,
            CorrelationId = correlationId ?? Guid.NewGuid().ToString("N")
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
        DateTime? toInclusive = null,
        string? user = null,
        string? entityType = null,
        string? outcome = null,
        string? source = null,
        string? correlationId = null,
        long? idAtMost = null,
        bool newestFirst = true,
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

        if (idAtMost is not null)
        {
            q = q.Where(x => x.Id <= idAtMost.Value);
        }

        if (fromInclusive is not null)
        {
            q = q.Where(x => x.Time >= fromInclusive.Value);
        }

        // 上界取「含」：调用方传的是当天最后一刻，写成 < 会把当天最后一毫秒的记录吞掉。
        if (toInclusive is not null)
        {
            q = q.Where(x => x.Time <= toInclusive.Value);
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var actionFilter = action.Trim();
            q = q.Where(x => x.Action == actionFilter);
        }

        if (!string.IsNullOrWhiteSpace(user))
        {
            var userFilter = user.Trim();
            q = q.Where(x => x.UserName == userFilter);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var entityFilter = entityType.Trim();
            q = q.Where(x => x.EntityType == entityFilter);
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var outcomeFilter = outcome.Trim();
            q = q.Where(x => x.Outcome == outcomeFilter);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var sourceFilter = source.Trim();
            q = q.Where(x => x.Source.Contains(sourceFilter));
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            var correlationFilter = correlationId.Trim();
            q = q.Where(x => x.CorrelationId == correlationFilter);
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
                || x.Outcome.Contains(k)
                || x.Source.Contains(k)
                || (x.SourceIp != null && x.SourceIp.Contains(k))
                || (x.CorrelationId != null && x.CorrelationId.Contains(k))
                || matchedActions.Contains(x.Action)
                || matchedEntities.Contains(x.EntityType));
        }

        var total = await q.CountAsync(cancellationToken).ConfigureAwait(false);

        var ordered = newestFirst
            ? q.OrderByDescending(x => x.Time).ThenByDescending(x => x.Id)
            : q.OrderBy(x => x.Time).ThenBy(x => x.Id);

        var items = await ordered
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return (items, total);
    }

    public async Task<long> GetLatestIdAsync(CancellationToken cancellationToken = default)
        => await _db.AuditLogs.AsNoTracking()
            .Select(x => x.Id)
            .OrderByDescending(id => id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<DateTime?> GetOldestTimeAsync(CancellationToken cancellationToken = default)
        => await _db.AuditLogs.AsNoTracking()
            .Select(x => (DateTime?)x.Time)
            .MinAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<int> DeleteRangeAsync(
        DateTime fromInclusive,
        DateTime toExclusive,
        long idAtMost,
        CancellationToken cancellationToken = default)
        => _db.AuditLogs
            .Where(x => x.Time >= fromInclusive && x.Time < toExclusive && x.Id <= idAtMost)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<(int Count, long? MinId, long? MaxId)> GetRangeStatsAsync(
        DateTime fromInclusive,
        DateTime toExclusive,
        long idAtMost,
        CancellationToken cancellationToken = default)
    {
        var query = _db.AuditLogs.AsNoTracking()
            .Where(x => x.Time >= fromInclusive
                     && x.Time < toExclusive
                     && x.Id <= idAtMost);
        var count = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return (0, null, null);
        }

        var minId = await query.MinAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
        var maxId = await query.MaxAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
        return (count, minId, maxId);
    }

    public async Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken cancellationToken = default)
        => await DistinctAsync(x => x.Action, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListUsersAsync(CancellationToken cancellationToken = default)
        => await DistinctAsync(x => x.UserName, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListEntityTypesAsync(CancellationToken cancellationToken = default)
        => await DistinctAsync(x => x.EntityType, cancellationToken).ConfigureAwait(false);

    /// <summary>某个字段的去重值。三个下拉共用一条查询，避免各写一遍 DISTINCT。</summary>
    private async Task<IReadOnlyList<string>> DistinctAsync(
        System.Linq.Expressions.Expression<Func<AuditLog, string>> selector,
        CancellationToken cancellationToken)
    {
        return await _db.AuditLogs.AsNoTracking()
            .Select(selector)
            .Where(v => v != null && v != "")
            .Distinct()
            .OrderBy(v => v)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
