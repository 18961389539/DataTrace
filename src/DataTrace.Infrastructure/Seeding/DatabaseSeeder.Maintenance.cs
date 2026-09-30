using System.Security.Cryptography;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Infrastructure.Identity;
using DataTrace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Seeding;

partial class DatabaseSeeder
{
    /// <summary>
    /// 一次性清理悬空/不可用的型号限值覆盖：Tag 已删，或 Tag 已改为 Bool/String。
    /// 幂等；日志打印清理行数，便于现场核对历史脏数据。
    /// </summary>
    /// <remarks>
    /// "哪些点位能配覆盖"这份名单来自 <see cref="RecipeLimitScope"/>，与限值对话框共用一份，
    /// 免得一边放行一边判成脏数据。
    /// </remarks>
    private async Task CleanupOrphanRecipeLimitsAsync(CancellationToken cancellationToken)
    {
        var validTagIds = await _db.Tags.AsNoTracking()
            .Where(t => RecipeLimitScope.NumericTypes.Contains(t.DataType))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var valid = validTagIds.ToHashSet();

        var orphans = await _db.RecipeLimits
            .Where(l => !valid.Contains(l.TagId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (orphans.Count == 0)
        {
            _logger.LogInformation("型号限值孤儿清理：无需处理（0 行）");
            return;
        }

        _db.RecipeLimits.RemoveRange(orphans);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning("型号限值孤儿清理：已删除 {Count} 行（点位不存在或已改为 Bool/String）", orphans.Count);
    }
}
