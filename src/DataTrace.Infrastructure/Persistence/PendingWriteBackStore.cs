using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

/// <summary>
/// 待确认回写留证的月库存取。留证跟着记录走（记录在哪个月库就存哪个月库），
/// 所以按工站查未确认时要跨月份库找最早的一条 —— 触发命中是每件一次的低频事件，这点开销值得换正确性。
/// </summary>
public sealed class PendingWriteBackStore : IPendingWriteBackStore
{
    private readonly RuntimeDbFactory _factory;

    public PendingWriteBackStore(RuntimeDbFactory factory)
    {
        _factory = factory;
    }

    public async Task SaveAsync(PendingWriteBack pending, CancellationToken cancellationToken = default)
    {
        await using var db = _factory.Open(pending.MonthKey);
        var existing = await db.PendingWriteBacks
            .FirstOrDefaultAsync(x => pending.CollectRecordId != null
                ? x.CollectRecordId == pending.CollectRecordId
                : x.CollectRecordId == null && x.SerialNo == pending.SerialNo && x.StationId == pending.StationId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            // 同一事件重复留证（比如重放路径与 finally 路径撞上）→ 保留首条事实，只刷新退避。
            existing.NextAttemptAt = pending.NextAttemptAt;
        }
        else
        {
            db.PendingWriteBacks.Add(pending);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PendingWriteBack?> FindOldestUnconfirmedAsync(int stationId, CancellationToken cancellationToken = default)
    {
        PendingWriteBack? oldest = null;
        foreach (var monthKey in _factory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey))
        {
            if (!_factory.Exists(monthKey))
            {
                continue;
            }

            await using var db = _factory.Open(monthKey);
            var candidate = await db.PendingWriteBacks.AsNoTracking()
                .Where(x => x.StationId == stationId && x.ConfirmedAt == null)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null && (oldest is null || candidate.CreatedAt < oldest.CreatedAt))
            {
                oldest = candidate;
            }
        }

        return oldest;
    }

    public async Task<IReadOnlyList<PendingWriteBack>> ListManualReviewAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PendingWriteBack>();
        foreach (var monthKey in _factory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey))
        {
            if (!_factory.Exists(monthKey)) continue;
            await using var db = _factory.Open(monthKey);
            result.AddRange(await db.PendingWriteBacks.AsNoTracking()
                .Where(x => x.ManualReviewRequired && x.ConfirmedAt == null)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }
        return result.OrderBy(x => x.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<PendingWriteBack>> ListUnconfirmedAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PendingWriteBack>();
        foreach (var monthKey in _factory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey))
        {
            if (!_factory.Exists(monthKey)) continue;
            await using var db = _factory.Open(monthKey);
            result.AddRange(await db.PendingWriteBacks.AsNoTracking()
                .Where(x => x.ConfirmedAt == null)
                .OrderBy(x => x.CreatedAt)
                .ToListAsync(cancellationToken).ConfigureAwait(false));
        }
        return result.OrderBy(x => x.CreatedAt).ToList();
    }

    public async Task DeleteAsync(string monthKey, long id, CancellationToken cancellationToken = default)
    {
        // 留证 Id 是月库内自增，跨月必然重复：只打开留证所属的那个月库，绝不按 Id 跨库扫描。
        if (!_factory.Exists(monthKey))
        {
            return;
        }

        await using var db = _factory.Open(monthKey);
        var pending = await db.PendingWriteBacks.FirstOrDefaultAsync(
                x => x.Id == id && x.ConfirmedAt == null && !x.ManualReviewRequired, cancellationToken)
            .ConfigureAwait(false);
        if (pending is not null)
        {
            db.PendingWriteBacks.Remove(pending);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteByRecordIdAsync(string monthKey, long collectRecordId, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey)) return;
        await using var db = _factory.Open(monthKey);
        var pending = await db.PendingWriteBacks.FirstOrDefaultAsync(
            x => x.CollectRecordId == collectRecordId && x.ConfirmedAt == null && !x.ManualReviewRequired,
            cancellationToken).ConfigureAwait(false);
        if (pending is null) return;
        db.PendingWriteBacks.Remove(pending);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailedAttemptAsync(string monthKey, long id, int attempts, DateTime nextAttemptAt, CancellationToken cancellationToken = default)
    {
        // 同 DeleteAsync：月库内自增 Id 必须配 MonthKey 才是完整身份，跨库扫描会更新到别的事件。
        if (!_factory.Exists(monthKey))
        {
            return;
        }

        await using var db = _factory.Open(monthKey);
        var pending = await db.PendingWriteBacks.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (pending is not null)
        {
            pending.Attempts = attempts;
            pending.NextAttemptAt = nextAttemptAt;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RequireManualReviewAsync(string monthKey, long id, string reason, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey)) return;
        await using var db = _factory.Open(monthKey);
        var pending = await db.PendingWriteBacks.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        if (pending is null || pending.ConfirmedAt is not null) return;
        pending.ManualReviewRequired = true;
        pending.ManualReviewRequiredAt ??= DateTime.Now;
        pending.ManualReviewReason ??= reason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RequireManualReviewByRecordIdAsync(string monthKey, long collectRecordId, string reason, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey)) return;
        await using var db = _factory.Open(monthKey);
        var pending = await db.PendingWriteBacks.FirstOrDefaultAsync(
            x => x.CollectRecordId == collectRecordId && x.ConfirmedAt == null, cancellationToken).ConfigureAwait(false);
        if (pending is null) return;
        pending.ManualReviewRequired = true;
        pending.ManualReviewRequiredAt ??= DateTime.Now;
        pending.ManualReviewReason ??= reason;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResolveManualReviewAsync(string monthKey, long id, string operatorName, string reason, string deviceStateEvidence, DateTime resolvedAt, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(monthKey)) return false;
        await using var db = _factory.Open(monthKey);
        var pending = await db.PendingWriteBacks.FirstOrDefaultAsync(x => x.Id == id, cancellationToken).ConfigureAwait(false);
        if (pending is null || !pending.ManualReviewRequired || pending.ConfirmedAt is not null) return false;
        pending.ManualReviewRequired = false;
        pending.ManualReviewResolvedAt = resolvedAt;
        pending.ManualReviewResolvedBy = operatorName;
        pending.ManualReviewResolution = reason;
        pending.ManualReviewDeviceStateEvidence = deviceStateEvidence;
        pending.ConfirmedAt = resolvedAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
