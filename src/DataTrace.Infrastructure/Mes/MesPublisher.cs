using DataTrace.Application.Mes;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Persistence;

namespace DataTrace.Infrastructure.Mes;

public sealed class MesPublisher : IMesPublisher
{
    private readonly ConfigDbContext _db;

    public MesPublisher(ConfigDbContext db)
    {
        _db = db;
    }

    public async Task EnqueueSessionAsync(string monthKey, long sessionId, string serialNo, string palletCode, string payloadJson, CancellationToken cancellationToken = default)
    {
        _db.MesOutbox.Add(new MesOutboxItem
        {
            MonthKey = monthKey,
            PalletSessionId = sessionId,
            SerialNo = serialNo,
            PalletCode = palletCode,
            CreatedAt = DateTime.Now,
            Status = MesOutboxStatus.Pending,
            PayloadJson = payloadJson
        });
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
