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
    public Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default)
        => ReadListAsync(
            db => db.Recipes.AsNoTracking()
                .Include(x => x.Limits)
                .OrderBy(x => x.Code)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public async Task<SavedRecipe> SaveRecipeAsync(SaveRecipeCommand command, CancellationToken cancellationToken = default)
    {
        var recipe = command.ToEntity();
        recipe.Code = recipe.Code.Trim();
        if (recipe.Id == 0 && recipe.Code.Length == 0)
        {
            recipe.Code = $"SYS-{Guid.NewGuid():N}".ToUpperInvariant();
        }

        recipe.Name = recipe.Name.Trim();

        // 编码是运行记录和曲线基线使用的内部键；旧调用方可继续传自定义码，新建空码时由仓储生成。
        if (RecipeCodeRules.Error(recipe.Code) is { } codeError)
        {
            throw new InvalidOperationException(codeError);
        }

        // 兼容旧调用方；用户界面要求填写名称。
        if (recipe.Name.Length == 0)
        {
            recipe.Name = recipe.Code;
        }

        await EnsureRecipeLimitsConsistentAsync(recipe.Code, recipe.Limits, cancellationToken).ConfigureAwait(false);

        if (recipe.Id == 0)
        {
            var createConflict = await _db.Recipes.AsNoTracking()
                .AnyAsync(x => x.Code.ToLower() == recipe.Code.ToLower(), cancellationToken)
                .ConfigureAwait(false);
            if (createConflict)
            {
                throw new InvalidOperationException($"型号编码「{recipe.Code}」已存在");
            }

            await ReleasePreviousCodeAsync(0, recipe.Code, cancellationToken).ConfigureAwait(false);

            // 先只落型号行，拿到主键后再同步限值 —— 与更新路径走同一套增量逻辑，
            // 避免级联插入和增量同步同时对同一批限值动手。
            var incoming = recipe.Limits.ToList();
            recipe.Limits = new List<RecipeLimit>();
            _db.Recipes.Add(recipe);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await SyncLimitsAsync(recipe.Id, incoming, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var existing = await _db.Recipes
                .FirstOrDefaultAsync(x => x.Id == recipe.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                return new SavedRecipe(recipe.Id, recipe.Code, recipe.Name);
            }

            var disabling = existing.Enabled && !recipe.Enabled;
            var oldCode = existing.Code;
            var newCode = recipe.Code;

            // 编码全局唯一（忽略大小写）；允许大小写校正同一条。
            var conflict = await _db.Recipes.AsNoTracking()
                .AnyAsync(x => x.Id != existing.Id && x.Code.ToLower() == newCode.ToLower(), cancellationToken)
                .ConfigureAwait(false);
            if (conflict)
            {
                throw new InvalidOperationException($"型号编码「{newCode}」已存在");
            }

            if (!string.Equals(oldCode, newCode, StringComparison.Ordinal))
            {
                // 历史采集记录保留旧码；把旧码记入 PreviousCodes，供曲线基线重建认领样本。
                var prev = string.IsNullOrWhiteSpace(existing.PreviousCodes)
                    ? []
                    : existing.PreviousCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (!prev.Contains(oldCode, StringComparer.Ordinal) && !string.IsNullOrEmpty(oldCode))
                {
                    prev.Add(oldCode);
                }
                // 若新码曾出现在历史列表里（来回改），去掉以免集合膨胀。
                prev.RemoveAll(c => string.Equals(c, newCode, StringComparison.Ordinal));
                existing.PreviousCodes = prev.Count == 0 ? null : string.Join(',', prev);
                existing.Code = newCode;
                _baselines.RetagRecipeCode(oldCode, newCode);
            }

            // 新码若正被别的型号记作历史编码，要收回来：一个编码在某一刻只能属于一个型号，
            // 否则两边都会把对方的样本算进自己的曲线基线。
            await ReleasePreviousCodeAsync(existing.Id, newCode, cancellationToken).ConfigureAwait(false);

            existing.Name = recipe.Name;
            existing.Enabled = recipe.Enabled;
            existing.Remark = recipe.Remark;
            await SyncLimitsAsync(existing.Id, recipe.Limits, cancellationToken).ConfigureAwait(false);

            if (disabling)
            {
                // 停用的正好是当前型号时清掉指针，避免留下"选着但已停用"这种看不出所以然的状态。
                var settings = await _db.SystemSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (settings?.ActiveRecipeId == existing.Id)
                {
                    settings.ActiveRecipeId = null;
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        await PushActiveRecipeToHubAsync(cancellationToken).ConfigureAwait(false);
        return new SavedRecipe(recipe.Id, recipe.Code, recipe.Name);
    }

    /// <summary>
    /// 只保存型号的限值覆盖行（传入集合即最终状态），不碰名称/启用状态/备注。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SaveRecipeAsync"/> 分开，是为了让限值编辑器不必把快照里的整份型号写回去：
    /// 那份快照可能是几分钟前读的，另一会话刚把这个型号停用/改名，一保存就会把旧值盖回去
    /// （甚至把已停用的型号重新启用，而当前型号指针早被清空，状态看上去毫无异常）。
    /// </remarks>
    public async Task SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<SaveRecipeLimitCommand> limits, CancellationToken cancellationToken = default)
    {
        var rows = limits.Select(x => x.ToEntity()).ToList();
        var recipe = await _db.Recipes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == recipeId, cancellationToken)
            .ConfigureAwait(false);
        if (recipe is null)
        {
            throw new InvalidOperationException($"型号 {recipeId} 不存在");
        }

        await EnsureRecipeLimitsConsistentAsync(recipe.Code, rows, cancellationToken).ConfigureAwait(false);
        await SyncLimitsAsync(recipeId, rows, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // 自增版本号 → 采集器下一轮拉到新快照、按新限值判定。
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 覆盖行必须与点位默认值<b>合并后</b>自洽：只查覆盖行自己是不成立的，
    /// 留空字段沿用点位默认值，"黄线跑到红线外"往往是改红线和改黄线各改了一半造成的。
    /// 与 <c>RecipeLimitDialog</c> 校验的是同一套口径（都走 <see cref="TagLimits.ConsistencyError"/>）。
    /// </summary>
    private async Task EnsureRecipeLimitsConsistentAsync(string code, IEnumerable<RecipeLimit> limits, CancellationToken cancellationToken)
    {
        var rows = limits.ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var tagIds = rows.Select(x => x.TagId).Distinct().ToList();
        var tags = await _db.Tags.AsNoTracking()
            .Where(t => tagIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var limit in rows)
        {
            var tag = tags.FirstOrDefault(t => t.Id == limit.TagId);
            if (tag is null)
            {
                // 点位已经不在配置库里：交给限值同步逻辑处理，这里不重复报同一个错。
                continue;
            }

            if (TagLimits.From(tag).Override(TagLimits.From(limit)).ConsistencyError() is { } error)
            {
                throw new InvalidOperationException($"型号 {code} 的点位 {tag.Name} 限值互相矛盾：{error}");
            }
        }
    }

    /// <summary>
    /// 把某个编码从其它型号的历史编码里收回来。
    /// </summary>
    /// <remarks>
    /// 编码唯一性原本只比对型号当前的编码：把 A100 改名为 B300（历史编码记下 A100）之后，
    /// 再新建一个 A100 是允许的，而 B300 的曲线基线仍然认 A100 的样本 ——
    /// 新旧两个型号的样本就串到一条基线里了。一个编码在某一刻只能属于一个型号，这里按后者收权。
    /// </remarks>
    private async Task ReleasePreviousCodeAsync(int recipeId, string code, CancellationToken cancellationToken)
    {
        var others = await _db.Recipes
            .Where(x => x.Id != recipeId && x.PreviousCodes != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var other in others)
        {
            var parts = other.PreviousCodes!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            // 与编码唯一性检查同一个口径：忽略大小写。
            if (parts.RemoveAll(p => string.Equals(p, code, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                continue;
            }

            other.PreviousCodes = parts.Count == 0 ? null : string.Join(',', parts);
        }
    }

    public async Task DeleteRecipeAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Recipes.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        _db.Recipes.Remove(item);

        // 删掉的正好是当前型号时顺手清空指针，不留悬空 id。
        var settings = await _db.SystemSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (settings?.ActiveRecipeId == id)
        {
            settings.ActiveRecipeId = null;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        await PushActiveRecipeToHubAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetActiveRecipeAsync(int? recipeId, CancellationToken cancellationToken = default)
    {
        if (recipeId is { } id)
        {
            var recipe = await _db.Recipes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                .ConfigureAwait(false);
            if (recipe is null)
            {
                throw new InvalidOperationException($"型号 {id} 不存在");
            }

            if (!recipe.Enabled)
            {
                throw new InvalidOperationException($"型号 {recipe.Code} 已停用，不能设为当前型号");
            }
        }

        var settings = await _db.SystemSettings.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (settings is null)
        {
            return;
        }

        settings.ActiveRecipeId = recipeId;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        // 自增版本号 → 采集器下一次轮询就会拉到新快照、换用新限值。
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
        await PushActiveRecipeToHubAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 限值覆盖行按主键做增量同步：传入的集合即最终状态。
    /// 与曲线判据同源的做法 —— 以数据库里的行为准，不读也不写调用方的导航集合。
    /// </summary>

    private async Task RemoveRecipeLimitsForTagAsync(int tagId, CancellationToken cancellationToken)
    {
        var orphans = await _db.RecipeLimits.Where(x => x.TagId == tagId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (orphans.Count == 0)
        {
            return;
        }

        _db.RecipeLimits.RemoveRange(orphans);
    }

    private async Task SyncLimitsAsync(int recipeId, IEnumerable<RecipeLimit> incoming, CancellationToken cancellationToken)
    {
        var targets = incoming.ToList();

        // 只收"确实存在且能配数值限值"的点位。删掉整台工站时点位是级联删除的，
        // 落进去的行会变成悬空覆盖：限值矩阵里根本看不到它，列表页的"覆盖点位 N 个"却照样计数，
        // 只能靠保存一次限值或重启时的清理才消失。
        if (targets.Count > 0)
        {
            var tagIds = targets.Select(x => x.TagId).Distinct().ToList();
            var overridable = await _db.Tags.AsNoTracking()
                .Where(t => tagIds.Contains(t.Id) && RecipeLimitScope.NumericTypes.Contains(t.DataType))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var valid = overridable.ToHashSet();
            targets = targets.Where(x => valid.Contains(x.TagId)).ToList();
        }

        var stored = await _db.RecipeLimits
            .Where(x => x.RecipeId == recipeId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = stored.ToDictionary(x => x.Id);
        var keep = new HashSet<int>();
        var seenTags = new HashSet<int>();

        foreach (var item in targets)
        {
            // (RecipeId, TagId) 上有唯一索引，同一点位重复提交只保留第一条。
            if (!seenTags.Add(item.TagId))
            {
                continue;
            }

            if (item.Id != 0 && byId.TryGetValue(item.Id, out var current))
            {
                CopyLimit(item, current);
                keep.Add(current.Id);
            }
            else
            {
                var fresh = new RecipeLimit { RecipeId = recipeId };
                CopyLimit(item, fresh);
                _db.RecipeLimits.Add(fresh);
            }
        }

        foreach (var stale in stored.Where(x => !keep.Contains(x.Id)).ToList())
        {
            _db.RecipeLimits.Remove(stale);
        }
    }

    private static void CopyLimit(RecipeLimit from, RecipeLimit to)
    {
        to.TagId = from.TagId;
        to.LowerLimit = from.LowerLimit;
        to.UpperLimit = from.UpperLimit;
        to.WarningLowerLimit = from.WarningLowerLimit;
        to.WarningUpperLimit = from.WarningUpperLimit;
        to.TargetValue = from.TargetValue;
    }

    /// <summary>
    /// 把当前生效型号立刻推到运行时看板 hub，不依赖采集器队列重建。
    /// 采集关闭或工站 Busy 时也能让 Dashboard 芯片即时刷新。
    /// </summary>
    private async Task PushActiveRecipeToHubAsync(CancellationToken cancellationToken)
    {
        var settings = await _db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (settings?.ActiveRecipeId is not { } activeId)
        {
            _status.SetActiveRecipe(null, null);
            return;
        }

        var recipe = await _db.Recipes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == activeId && x.Enabled, cancellationToken)
            .ConfigureAwait(false);
        _status.SetActiveRecipe(recipe?.Code, recipe?.Name);
    }
}
