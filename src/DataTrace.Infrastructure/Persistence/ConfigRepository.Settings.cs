using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

partial class ConfigRepository
{
    public async Task SaveSettingsAsync(SaveSettingsCommand command, CancellationToken cancellationToken = default)
    {
        var settings = command.ToEntity();
        // 界面的 Min/Max 只是输入框行为，脚本与历史脏数据可以直接写库：
        // 保留年数为 0 会被清理任务当成 1 年（删数据），扫描间隔为 0 会被采集端当成 20ms（压垮 PLC 通讯）。
        if (SettingsLimits.Error(settings) is { } error)
        {
            throw new InvalidOperationException(error);
        }

        if (settings.Id == 0)
        {
            _db.SystemSettings.Add(settings);
        }
        else
        {
            var existing = await _db.SystemSettings
                .FirstOrDefaultAsync(x => x.Id == settings.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.SystemSettings.Add(settings);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(settings);
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        // 采集开关等运行态字段变更时立刻唤醒看板，不依赖采集器循环。
        _status.NotifyChanged();
        await PushActiveRecipeToHubAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<MesOutboxSnapshot> GetMesOutboxStatusAsync(CancellationToken cancellationToken = default)
        => ReadAsync(db => LoadMesOutboxStatusAsync(db, cancellationToken), cancellationToken);

    private static async Task<MesOutboxSnapshot> LoadMesOutboxStatusAsync(ConfigDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.MesOutbox.AsNoTracking()
            .Where(x => x.Status == MesOutboxStatus.Pending)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Oldest = g.Min(x => x.CreatedAt) })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // 成功与失败都写 LastAttemptAt，所以按它倒序取第一行就是"最近一次尝试"。
        var lastAttempt = await db.MesOutbox.AsNoTracking()
            .Where(x => x.LastAttemptAt != null)
            .OrderByDescending(x => x.LastAttemptAt)
            .Select(x => new { x.LastAttemptAt, x.Status, x.LastError })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var lastSuccessAt = await db.MesOutbox.AsNoTracking()
            .Where(x => x.Status == MesOutboxStatus.Succeeded && x.LastAttemptAt != null)
            .OrderByDescending(x => x.LastAttemptAt)
            .Select(x => x.LastAttemptAt)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new MesOutboxSnapshot
        {
            PendingCount = pending?.Count ?? 0,
            OldestPendingAt = pending?.Oldest,
            LastAttemptAt = lastAttempt?.LastAttemptAt,
            LastAttemptSucceeded = lastAttempt is null ? null : lastAttempt.Status == MesOutboxStatus.Succeeded,
            LastError = lastAttempt?.LastError,
            LastSuccessAt = lastSuccessAt
        };
    }

    public async Task BumpVersionAsync(CancellationToken cancellationToken = default)
    {
        var row = await _db.ConfigVersions.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            _db.ConfigVersions.Add(new ConfigVersion { Version = 1 });
        }
        else
        {
            row.Version++;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
