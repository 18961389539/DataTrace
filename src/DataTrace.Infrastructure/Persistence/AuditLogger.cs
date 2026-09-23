using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;

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

    public async Task<IReadOnlyList<AuditLog>> QueryAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(
                _db.AuditLogs.OrderByDescending(x => x.Time).Take(take),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
