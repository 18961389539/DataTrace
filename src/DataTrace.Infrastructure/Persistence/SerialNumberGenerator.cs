using DataTrace.Application.Runtime;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class SerialNumberGenerator : ISerialNumberGenerator
{
    private readonly ConfigDbContext _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SerialNumberGenerator(ConfigDbContext db)
    {
        _db = db;
    }

    public async Task<string> NextAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        var dayKey = date.ToString("yyyyMMdd");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var row = await _db.SerialCounters.FirstOrDefaultAsync(x => x.DayKey == dayKey, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                row = new Domain.Entities.SerialCounter { DayKey = dayKey, LastValue = 0 };
                _db.SerialCounters.Add(row);
            }

            row.LastValue++;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return $"{dayKey}-{row.LastValue:000000}";
        }
        finally
        {
            _gate.Release();
        }
    }
}
