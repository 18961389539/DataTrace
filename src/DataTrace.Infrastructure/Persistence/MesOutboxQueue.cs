using DataTrace.Application.Mes;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class MesOutboxQueue : IMesOutboxQueue
{
    private readonly IDbContextFactory<ConfigDbContext> _factory;

    public MesOutboxQueue(IDbContextFactory<ConfigDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<MesOutboxPending>> TakePendingAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.MesOutbox.AsNoTracking()
            .Where(x => x.Status == MesOutboxStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .Take(take)
            .Select(x => new MesOutboxPending
            {
                Id = x.Id,
                SerialNo = x.SerialNo,
                PalletCode = x.PalletCode,
                PalletSessionId = x.PalletSessionId,
                PayloadJson = x.PayloadJson,
                AttemptCount = x.AttemptCount,
                LastAttemptAt = x.LastAttemptAt
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveAttemptsAsync(IReadOnlyList<MesOutboxAttempt> attempts, CancellationToken cancellationToken = default)
    {
        if (attempts.Count == 0)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var ids = attempts.Select(x => x.Id).ToList();
        var rows = await db.MesOutbox
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = rows.ToDictionary(x => x.Id);
        foreach (var attempt in attempts)
        {
            if (!byId.TryGetValue(attempt.Id, out var row))
            {
                continue;
            }

            row.AttemptCount = attempt.AttemptCount;
            row.LastAttemptAt = attempt.AttemptedAt;
            if (attempt.Succeeded)
            {
                row.Status = MesOutboxStatus.Succeeded;
                row.LastError = null;
            }
            else
            {
                row.LastError = attempt.Error;
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
