using DataTrace.Application.Mes;
using DataTrace.Application.Runtime;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class MesOutboxRelay : IMesOutboxRelay
{
    private readonly RuntimeDbFactory _runtimeFactory;
    private readonly IMesOutboxQueue _queue;
    private readonly IDataMaintenanceBarrier? _maintenance;

    public MesOutboxRelay(RuntimeDbFactory runtimeFactory, IMesOutboxQueue queue, IDataMaintenanceBarrier? maintenance = null)
    {
        _runtimeFactory = runtimeFactory;
        _queue = queue;
        _maintenance = maintenance;
    }

    public async Task<int> RelayPendingAsync(int takePerMonth, CancellationToken cancellationToken = default)
    {
        await using var lease = _maintenance is null ? null : await _maintenance.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var relayed = 0;
        foreach (var monthKey in _runtimeFactory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey))
        {
            await using var db = _runtimeFactory.Open(monthKey);
            var pending = await db.MesOutboxIntents
                .Where(x => x.PublishedAt == null)
                .OrderBy(x => x.CreatedAt)
                .Take(takePerMonth)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var intent in pending)
            {
                await _queue.EnqueueIfMissingAsync(new MesOutboxMessage
                {
                    EventId = intent.EventId,
                    MonthKey = intent.MonthKey,
                    PalletSessionId = intent.PalletSessionId,
                    SerialNo = intent.SerialNo,
                    PalletCode = intent.PalletCode,
                    CreatedAt = intent.CreatedAt,
                    PayloadJson = intent.PayloadJson
                }, cancellationToken).ConfigureAwait(false);
                intent.PublishedAt = DateTime.Now;
                relayed++;
            }

            if (pending.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return relayed;
    }
}
