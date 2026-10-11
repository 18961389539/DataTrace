using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class ActiveSessionStore : IActiveSessionStore
{
    private readonly ConfigDbContext _db;

    public ActiveSessionStore(ConfigDbContext db)
    {
        _db = db;
    }

    public Task<ActiveSessionIndex?> FindByPalletAsync(string palletCode, CancellationToken cancellationToken = default)
        => _db.ActiveSessions.FirstOrDefaultAsync(x => x.PalletCode == palletCode, cancellationToken);

    public async Task UpsertAsync(ActiveSessionIndex session, CancellationToken cancellationToken = default)
    {
        var existing = await _db.ActiveSessions.FirstOrDefaultAsync(x => x.PalletCode == session.PalletCode, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _db.ActiveSessions.Add(session);
        }
        else
        {
            existing.SerialNo = session.SerialNo;
            existing.SessionId = session.SessionId;
            existing.MonthKey = session.MonthKey;
            existing.StartTime = session.StartTime;
            existing.RecipeCode = session.RecipeCode ?? "";
            // 快照只在首站写一次：后续工站的索引更新不带快照，null 时保留首站固化的参数，
            // 不能被覆盖成空 —— 否则“冻结”就只剩名字。
            existing.RecipeSnapshot = session.RecipeSnapshot ?? existing.RecipeSnapshot;
            existing.LastStationId = session.LastStationId;
            existing.LastStationCode = session.LastStationCode ?? "";
            existing.LastActivityAt = session.LastActivityAt;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveByPalletAsync(string palletCode, CancellationToken cancellationToken = default)
    {
        var existing = await _db.ActiveSessions.FirstOrDefaultAsync(x => x.PalletCode == palletCode, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        _db.ActiveSessions.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ActiveSessionIndex>> ListAsync(CancellationToken cancellationToken = default)
        => await _db.ActiveSessions.AsNoTracking().OrderBy(x => x.StartTime).ToListAsync(cancellationToken).ConfigureAwait(false);
}
