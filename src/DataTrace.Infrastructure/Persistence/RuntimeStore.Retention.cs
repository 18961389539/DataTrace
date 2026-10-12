using DataTrace.Application.Alarms;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class RuntimeStore
{
    public IReadOnlyList<string> ListMonthKeys() => _factory.ListMonthKeys();

    /// <summary>
    /// 保留删除前的阻断检查（R10）：按"这个月库还被谁引用"逐项清点，
    /// 而不是只看月库名是否过了保留年限 —— 跨月会话的新记录、未确认留证、
    /// 未决意图、未投递 MES 都可能让一个"过期"的月库仍是活数据。
    /// </summary>
    public async Task<IReadOnlyList<string>> GetDeletionBlockersAsync(
        string monthKey, DateTime cutoff, CancellationToken cancellationToken = default)
    {
        var blockers = new List<string>();
        if (!_factory.Exists(monthKey))
        {
            return blockers;
        }

        await using var db = _factory.Open(monthKey);

        var pending = await db.PendingWriteBacks.CountAsync(x => x.ConfirmedAt == null, cancellationToken).ConfigureAwait(false);
        if (pending > 0)
        {
            blockers.Add($"{pending} unconfirmed write-back confirmation(s) stored in this month");
        }

        var intents = await db.SessionIndexIntents
            .CountAsync(x => x.AppliedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (intents > 0)
        {
            blockers.Add($"{intents} unapplied session index intent(s) stored in this month");
        }

        var outbox = await db.MesOutboxIntents
            .CountAsync(x => x.PublishedAt == null, cancellationToken)
            .ConfigureAwait(false);
        if (outbox > 0)
        {
            blockers.Add($"{outbox} unpublished MES outbox intent(s) reference this month");
        }

        var openSessions = await db.PalletSessions
            .Where(x => x.Status != SessionStatus.Closed || x.EndTime == null)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        if (openSessions > 0)
        {
            blockers.Add($"{openSessions} open pallet session(s) still live in this month");
        }

        if (await db.CollectRecords.AnyAsync(x => x.TriggerTime >= cutoff, cancellationToken).ConfigureAwait(false))
        {
            blockers.Add("records newer than the retention cutoff exist; a cross-month session may still be writing here");
        }

        var active = await _activeSessions.ListAsync(cancellationToken).ConfigureAwait(false);
        if (active.Any(x => x.MonthKey == monthKey)) blockers.Add("active session index references this month");

        return blockers;
    }

    public async Task<(int Curves, int Archives)> GetExternalFileReferenceCountsAsync(
        string fileMonthKey, string excludingRuntimeMonthKey, CancellationToken cancellationToken = default)
    {
        if (fileMonthKey.Length != 6 || !fileMonthKey.All(char.IsAsciiDigit))
            throw new ArgumentException("File month key must be yyyyMM", nameof(fileMonthKey));
        var prefix = $"{fileMonthKey[..4]}/{fileMonthKey[4..]}/";
        var legacyPrefix = $"{fileMonthKey[..4]}\\{fileMonthKey[4..]}\\";
        var curves = 0;
        var archives = 0;
        foreach (var monthKey in _factory.ListMonthKeys().Where(RuntimeDbFactory.IsValidMonthKey)
                     .Where(key => !string.Equals(key, excludingRuntimeMonthKey, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_factory.Exists(monthKey)) continue;
            await using var db = _factory.Open(monthKey);
            curves += await db.CurveRecords.CountAsync(
                x => x.RelativePath.StartsWith(prefix) || x.RelativePath.StartsWith(legacyPrefix), cancellationToken)
                .ConfigureAwait(false);
            archives += await db.CollectRecords.CountAsync(
                x => x.ArchivePath != "" && (x.ArchivePath.StartsWith(prefix) || x.ArchivePath.StartsWith(legacyPrefix)), cancellationToken)
                .ConfigureAwait(false);
        }

        return (curves, archives);
    }

    public async Task<IReadOnlyList<string>> GetExternalFileMonthKeysAsync(
        string runtimeMonthKey, CancellationToken cancellationToken = default)
    {
        if (!_factory.Exists(runtimeMonthKey)) return [];
        await using var db = _factory.Open(runtimeMonthKey);
        var paths = await db.CurveRecords.AsNoTracking().Select(x => x.RelativePath).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        paths.AddRange(await db.CollectRecords.AsNoTracking().Where(x => x.ArchivePath != "")
            .Select(x => x.ArchivePath).ToListAsync(cancellationToken).ConfigureAwait(false));
        return paths.Select(TryGetFileMonthKey).Where(x => x is not null).Cast<string>()
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    private static string? TryGetFileMonthKey(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].Length != 4 || parts[1].Length != 2
            || !parts[0].All(char.IsAsciiDigit) || !parts[1].All(char.IsAsciiDigit)) return null;
        var month = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 ? parts[0] + parts[1] : null;
    }

    public Task DeleteMonthAsync(string monthKey, CancellationToken cancellationToken = default)
    {
        _factory.DeleteMonth(monthKey);
        InvalidateQueryCountCache();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sessions keep their records in the month database where the session began.
    /// Time-based queries must therefore search every retained database and filter by TriggerTime.
    /// </summary>
    private List<string> ExistingMonthKeys()
        => _factory.ListMonthKeys()
            .Where(RuntimeDbFactory.IsValidMonthKey)
            .Where(_factory.Exists)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
}
