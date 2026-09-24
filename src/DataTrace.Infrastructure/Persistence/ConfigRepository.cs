using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class ConfigRepository : IConfigRepository
{
    private readonly ConfigDbContext _db;
    private readonly ICurveBaselineCache _baselines;

    public ConfigRepository(ConfigDbContext db, ICurveBaselineCache baselines)
    {
        _db = db;
        _baselines = baselines;
    }

    public Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
        => _db.ConfigVersions.AsNoTracking().Select(x => x.Version).FirstOrDefaultAsync(cancellationToken);

    public async Task<AppConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                       ?? new SystemSettings();
        var plcs = await _db.PlcConnections
            .AsNoTracking()
            .Include(x => x.Heartbeat)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var stations = await _db.Stations
            .AsNoTracking()
            .Include(x => x.Positions)
            .Include(x => x.Tags)
            .Include(x => x.Curves).ThenInclude(c => c.Series)
            .Include(x => x.Curves).ThenInclude(c => c.Criteria)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var version = await _db.ConfigVersions.AsNoTracking().Select(x => x.Version).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var recipes = await _db.Recipes
            .AsNoTracking()
            .Include(x => x.Limits)
            .OrderBy(x => x.Code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // 停用的型号不算生效：宁可退回点位默认限值，也不要悄悄按一个已停用的型号判定。
        var activeRecipe = settings.ActiveRecipeId is { } activeId
            ? recipes.FirstOrDefault(x => x.Id == activeId && x.Enabled)
            : null;

        return new AppConfigurationSnapshot
        {
            Settings = settings,
            PlcConnections = plcs,
            Stations = stations,
            Recipes = recipes,
            ActiveRecipe = activeRecipe,
            Version = version
        };
    }

    public Task<IReadOnlyList<Recipe>> GetRecipesAsync(CancellationToken cancellationToken = default)
        => MapList(_db.Recipes.AsNoTracking()
            .Include(x => x.Limits)
            .OrderBy(x => x.Code)
            .ToListAsync(cancellationToken));

    public Task<IReadOnlyList<PlcConnection>> GetPlcConnectionsAsync(CancellationToken cancellationToken = default)
        => MapList(_db.PlcConnections.AsNoTracking().Include(x => x.Heartbeat).ToListAsync(cancellationToken));

    public Task<PlcConnection?> GetPlcConnectionAsync(int id, CancellationToken cancellationToken = default)
        => _db.PlcConnections.Include(x => x.Heartbeat).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task SavePlcConnectionAsync(PlcConnection connection, CancellationToken cancellationToken = default)
    {
        if (connection.Id == 0)
        {
            _db.PlcConnections.Add(connection);
        }
        else
        {
            _db.PlcConnections.Update(connection);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePlcConnectionAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.PlcConnections.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        _db.PlcConnections.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Station>> GetStationsAsync(CancellationToken cancellationToken = default)
        => MapList(_db.Stations.AsNoTracking()
            .Include(x => x.PlcConnection)
            .Include(x => x.Positions)
            .Include(x => x.Tags)
            .Include(x => x.Curves).ThenInclude(c => c.Series)
            .Include(x => x.Curves).ThenInclude(c => c.Criteria)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken));

    public Task<Station?> GetStationAsync(int id, CancellationToken cancellationToken = default)
        => _db.Stations
            .Include(x => x.Positions)
            .Include(x => x.Tags)
            .Include(x => x.Curves).ThenInclude(c => c.Series)
            .Include(x => x.Curves).ThenInclude(c => c.Criteria)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task SaveStationAsync(Station station, CancellationToken cancellationToken = default)
    {
        SyncPositions(station);
        var entry = _db.Entry(station);
        if (station.Id == 0)
        {
            _db.Stations.Add(station);
        }
        else if (entry.State == EntityState.Detached)
        {
            var existing = await _db.Stations
                .Include(x => x.Positions)
                .FirstOrDefaultAsync(x => x.Id == station.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.Stations.Add(station);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(station);
                existing.Positions.Clear();
                foreach (var p in station.Positions)
                {
                    existing.Positions.Add(p);
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteStationAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Stations.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        _db.Stations.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveTagAsync(TagDefinition tag, CancellationToken cancellationToken = default)
    {
        if (tag.PositionIndex < 0)
        {
            tag.PositionIndex = 0;
        }
        else if (tag.PositionIndex > 1)
        {
            tag.PositionIndex = 1;
        }

        // 对话框里也校验这一条，但那只是 UI：从 MES 或其它入口直接写库照样能留下自相矛盾的限值，
        // 而采集端只会默默把黄线收敛进红线，明细页上显示的预警限就跟实际判据不是一回事了。
        if (TagLimits.From(tag).ConsistencyError() is { } error)
        {
            throw new InvalidOperationException($"点位 {tag.Code} 的限值互相矛盾：{error}");
        }

        if (tag.Id == 0)
        {
            _db.Tags.Add(tag);
        }
        else
        {
            // 不要 Update(detached)：同作用域里若已有同 Id 跟踪实例会触发 EF 冲突。
            // 与 SaveCurveAsync 一致：加载已跟踪实体再 SetValues。
            var existing = await _db.Tags.FirstOrDefaultAsync(x => x.Id == tag.Id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                _db.Tags.Add(tag);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(tag);
            }

            // Bool/String 不能参与数值限值覆盖：改类型时清掉遗留 RecipeLimit
            if (tag.DataType is PlcDataType.Bool or PlcDataType.String)
            {
                await RemoveRecipeLimitsForTagAsync(tag.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTagAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Tags.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        // 同一事务清掉型号覆盖行，避免 TagId 悬空孤儿。
        await RemoveRecipeLimitsForTagAsync(id, cancellationToken).ConfigureAwait(false);
        _db.Tags.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCurveAsync(CurveDefinition curve, CancellationToken cancellationToken = default)
    {
        curve.PositionIndex = 1;
        if (curve.Id == 0)
        {
            _db.Curves.Add(curve);
        }
        else
        {
            var existing = await _db.Curves
                .Include(x => x.Series)
                .FirstOrDefaultAsync(x => x.Id == curve.Id, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                _db.Curves.Add(curve);
            }
            else
            {
                _db.Entry(existing).CurrentValues.SetValues(curve);
                existing.Series.Clear();
                foreach (var s in curve.Series)
                {
                    existing.Series.Add(s);
                }
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveCurveCriteriaAsync(int curveId, IReadOnlyList<CurveCriterion> criteria, CancellationToken cancellationToken = default)
    {
        if (!await _db.Curves.AnyAsync(x => x.Id == curveId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await SyncCriteriaAsync(curveId, criteria, cancellationToken).ConfigureAwait(false);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 判据按主键做增量同步：传入的集合即最终状态。
    /// 判定增删改一律以**数据库里的行**为准，不读也不写调用方的导航集合 ——
    /// 调用方手上的往往是同一个被跟踪实例，替换它的集合会和 EF 的导航修正互相打架：
    /// Clear + 重新 Add 会让同一主键在一次 SaveChanges 里既删除又插入，
    /// 而把克隆实体塞进导航集合则会让同一行在集合里留下两份（一份是永不落库的幽灵）。
    /// </summary>
    private async Task SyncCriteriaAsync(int curveId, IEnumerable<CurveCriterion> incoming, CancellationToken cancellationToken)
    {
        var targets = incoming.ToList();
        var stored = await _db.CurveCriteria
            .Where(x => x.CurveDefinitionId == curveId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byId = stored.ToDictionary(x => x.Id);
        var keep = new HashSet<int>();

        foreach (var item in targets)
        {
            if (item.Id != 0 && byId.TryGetValue(item.Id, out var current))
            {
                CopyCriterion(item, current);
                keep.Add(current.Id);
            }
            else
            {
                // 新增行一律新建实体交给 EF 挂载：传进来的可能是编辑用的克隆体，
                // 直接复用会让克隆体进入导航集合而真正的跟踪实体另有一份。
                var fresh = new CurveCriterion { CurveDefinitionId = curveId };
                CopyCriterion(item, fresh);
                _db.CurveCriteria.Add(fresh);
            }
        }

        foreach (var stale in stored.Where(x => !keep.Contains(x.Id)).ToList())
        {
            _db.CurveCriteria.Remove(stale);
        }
    }

    private static void CopyCriterion(CurveCriterion from, CurveCriterion to)
    {
        to.SeriesName = from.SeriesName;
        to.Enabled = from.Enabled;
        to.PeakMin = from.PeakMin;
        to.PeakMax = from.PeakMax;
        to.MeanMin = from.MeanMin;
        to.MeanMax = from.MeanMax;
        to.AreaMin = from.AreaMin;
        to.AreaMax = from.AreaMax;
        to.RiseSlopeMin = from.RiseSlopeMin;
        to.RiseSlopeMax = from.RiseSlopeMax;
        to.HoldSlopeMin = from.HoldSlopeMin;
        to.HoldSlopeMax = from.HoldSlopeMax;
        to.FallRatioMax = from.FallRatioMax;
        to.MaxStepMax = from.MaxStepMax;
        to.StdDevMax = from.StdDevMax;
        to.OscillationMax = from.OscillationMax;
    }

    public async Task DeleteCurveAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Curves.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        _db.Curves.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        recipe.Code = recipe.Code.Trim();
        recipe.Name = recipe.Name.Trim();

        await EnsureRecipeLimitsConsistentAsync(recipe, cancellationToken).ConfigureAwait(false);

        if (recipe.Id == 0)
        {
            var createConflict = await _db.Recipes.AsNoTracking()
                .AnyAsync(x => x.Code.ToLower() == recipe.Code.ToLower(), cancellationToken)
                .ConfigureAwait(false);
            if (createConflict)
            {
                throw new InvalidOperationException($"型号编码「{recipe.Code}」已存在");
            }

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
                return;
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
    }

    /// <summary>
    /// 覆盖行必须与点位默认值<b>合并后</b>自洽：只查覆盖行自己是不成立的，
    /// 留空字段沿用点位默认值，"黄线跑到红线外"往往是改红线和改黄线各改了一半造成的。
    /// 与 <c>RecipeLimitDialog</c> 校验的是同一套口径（都走 <see cref="TagLimits.ConsistencyError"/>）。
    /// </summary>
    private async Task EnsureRecipeLimitsConsistentAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        if (recipe.Limits.Count == 0)
        {
            return;
        }

        var tagIds = recipe.Limits.Select(x => x.TagId).Distinct().ToList();
        var tags = await _db.Tags.AsNoTracking()
            .Where(t => tagIds.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var limit in recipe.Limits)
        {
            var tag = tags.FirstOrDefault(t => t.Id == limit.TagId);
            if (tag is null)
            {
                // 点位已经不在配置库里：交给限值同步逻辑处理，这里不重复报同一个错。
                continue;
            }

            if (TagLimits.From(tag).Override(TagLimits.From(limit)).ConsistencyError() is { } error)
            {
                throw new InvalidOperationException($"型号 {recipe.Code} 的点位 {tag.Code} 限值互相矛盾：{error}");
            }
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

    public async Task SaveHeartbeatAsync(HeartbeatSettings heartbeat, CancellationToken cancellationToken = default)
    {
        if (heartbeat.Id == 0)
        {
            _db.Heartbeats.Add(heartbeat);
        }
        else
        {
            _db.Heartbeats.Update(heartbeat);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(SystemSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Id == 0)
        {
            _db.SystemSettings.Add(settings);
        }
        else
        {
            _db.SystemSettings.Update(settings);
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
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

    private static void SyncPositions(Station station)
    {
        station.PositionCount = 1;
        var keep = station.Positions.OrderBy(x => x.Index).FirstOrDefault(x => x.Index == 1)
                   ?? station.Positions.OrderBy(x => x.Index).FirstOrDefault();
        station.Positions.Clear();
        if (keep is null)
        {
            keep = new ProductPositionDefinition { Index = 1, Name = "产品" };
        }
        else
        {
            keep.Index = 1;
            keep.OccupiedAddress = null;
            if (string.IsNullOrWhiteSpace(keep.Name) || keep.Name.StartsWith("产品位", StringComparison.Ordinal))
            {
                keep.Name = "产品";
            }
        }

        station.Positions.Add(keep);
    }

    private static async Task<IReadOnlyList<T>> MapList<T>(Task<List<T>> task) => await task.ConfigureAwait(false);
}
