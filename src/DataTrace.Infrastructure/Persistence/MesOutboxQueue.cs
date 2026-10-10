using DataTrace.Application.Mes;
using DataTrace.Domain.Entities;
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

    public async Task EnqueueIfMissingAsync(MesOutboxMessage message, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "MesOutbox" (
                "EventId", "MonthKey", "PalletSessionId", "SerialNo", "PalletCode", "CreatedAt",
                "LastAttemptAt", "NextAttemptAt", "AttemptCount", "Status", "PayloadJson", "LastError")
            VALUES (
                {message.EventId}, {message.MonthKey}, {message.PalletSessionId}, {message.SerialNo},
                {message.PalletCode}, {message.CreatedAt}, NULL, NULL, 0, {(int)MesOutboxStatus.Pending},
                {message.PayloadJson}, NULL)
            ON CONFLICT ("EventId") DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MesOutboxPending>> TakePendingAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.Now;
        return await db.MesOutbox.AsNoTracking()
            .Where(x => x.Status == MesOutboxStatus.Pending && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(take)
            .Select(x => new MesOutboxPending
            {
                Id = x.Id,
                EventId = x.EventId,
                SerialNo = x.SerialNo,
                MonthKey = x.MonthKey,
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
            row.NextAttemptAt = attempt.NextAttemptAt;
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
