using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

public sealed class ConfigRepository : IConfigRepository
{
    private readonly ConfigDbContext _db;
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly ICurveBaselineCache _baselines;
    private readonly IRuntimeStatusHub _status;

    /// <summary>上一个版本号的整图。只在本仓储（一个电路/一次作用域）内复用，见 <see cref="GetSnapshotAsync"/>。</summary>
    private readonly object _snapshotGate = new();
    private AppConfigurationSnapshot? _cachedSnapshot;

    public ConfigRepository(
        ConfigDbContext db,
        IDbContextFactory<ConfigDbContext> factory,
        ICurveBaselineCache baselines,
        IRuntimeStatusHub status)
    {
        _db = db;
        _factory = factory;
        _baselines = baselines;
        _status = status;
    }

    /// <summary>
    /// 只读查询各自开一个临时 context，用完即弃。
    /// </summary>
    /// <remarks>
    /// scoped 的 <see cref="_db"/> 在 Blazor Server 里是整个电路共用的，而 EF 的 DbContext 不支持并发：
    /// 读也挤在它上面时，页面上任何"并行取数"都会撞车（报表页就因此被迫把配置读串行在前）。
    /// 走工厂之后读与读、读与写互不干扰，页面想并行就并行；
    /// 写路径仍然只用 <see cref="_db"/>，它才需要"改完立刻在同一上下文里看到自己改的东西"。
    /// </remarks>
    private async Task<T> ReadAsync<T>(Func<ConfigDbContext, Task<T>> read, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await read(db).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(
        Func<ConfigDbContext, Task<List<T>>> read,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await read(db).ConfigureAwait(false);
    }

    public Task<int> GetVersionAsync(CancellationToken cancellationToken = default)
        => ReadAsync(db => db.ConfigVersions.AsNoTracking().Select(x => x.Version).FirstOrDefaultAsync(cancellationToken), cancellationToken);

    public async Task<AppConfigurationSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        // 先读版本号、再读整图，顺序不能反：反过来的话可能把"新版本号 + 旧内容"存进缓存，
        // 此后每次都命中这份错位的快照（版本号没变就不重读）。按现在的顺序，最坏只是多读一次整图。
        var version = await GetVersionAsync(cancellationToken).ConfigureAwait(false);

        // 版本号没变就复用上次那份图：整图要跑五条查询，而看板一次加载里会问好几次、每次 hub 事件也要问。
        // 缓存<b>只留在本实例</b>（一个电路/一次作用域），刻意不做成全局单例 ——
        // 版本号只由仓储的写操作自增，直接改库（现场用工具改、或换回一个旧库文件）不会让它变，
        // 全局缓存会把"改过的库"整片挡住，直到有人从界面保存一次为止；按作用域缓存，
        // 每个新作用域（后台服务每一轮、新开的电路）第一眼读到的都是库里的真值。
        lock (_snapshotGate)
        {
            if (_cachedSnapshot is { } cached && cached.Version == version)
            {
                return cached;
            }
        }

        var snapshot = await ReadAsync(
            db => LoadSnapshotAsync(db, version, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        lock (_snapshotGate)
        {
            _cachedSnapshot = snapshot;
        }

        return snapshot;
    }

    private static async Task<AppConfigurationSnapshot> LoadSnapshotAsync(
        ConfigDbContext db,
        int version,
        CancellationToken cancellationToken)
    {
        var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                       ?? new SystemSettings();
        var plcs = await db.PlcConnections
            .AsNoTracking()
            .Include(x => x.Heartbeat)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var stations = await db.Stations
            .AsNoTracking()
            .Include(x => x.Positions)
            .Include(x => x.Tags)
            .Include(x => x.Curves).ThenInclude(c => c.Series)
            .Include(x => x.Curves).ThenInclude(c => c.Criteria)
            .OrderBy(x => x.Sequence)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var recipes = await db.Recipes
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
        => ReadListAsync(
            db => db.Recipes.AsNoTracking()
                .Include(x => x.Limits)
                .OrderBy(x => x.Code)
                .ToListAsync(cancellationToken),
            cancellationToken);

    public Task<IReadOnlyList<PlcConnection>> GetPlcConnectionsAsync(CancellationToken cancellationToken = default)
        => ReadListAsync(
            db => db.PlcConnections.AsNoTracking().Include(x => x.Heartbeat).ToListAsync(cancellationToken),
            cancellationToken);

    /// <summary>
    /// 单个 PLC 的详情读：与 <see cref="GetStationAsync"/> 一样保留在 scoped 上下文里，本次不动。
    /// </summary>
    public Task<PlcConnection?> GetPlcConnectionAsync(int id, CancellationToken cancellationToken = default)
        => _db.PlcConnections.Include(x => x.Heartbeat).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task SavePlcConnectionAsync(PlcConnection connection, CancellationToken cancellationToken = default)
    {
        connection.Name = connection.Name.Trim();

        // 库里 Name 上有唯一索引，重名会抛出 SQLite 的原始错误；先在这里查出来，
        // 用户看到的才是"哪台重名"而不是一串 SQL 报错（与型号保存同一套做法）。
        if (await _db.PlcConnections.AnyAsync(
                x => x.Id != connection.Id && x.Name == connection.Name,
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"PLC 名称「{connection.Name}」已存在");
        }

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
        => ReadListAsync(
            db => db.Stations.AsNoTracking()
                .Include(x => x.PlcConnection)
                .Include(x => x.Positions)
                .Include(x => x.Tags)
                .Include(x => x.Curves).ThenInclude(c => c.Series)
                .Include(x => x.Curves).ThenInclude(c => c.Criteria)
                .OrderBy(x => x.Sequence)
                .ToListAsync(cancellationToken),
            cancellationToken);

    /// <summary>
    /// 单个工站的详情读：刻意留在 scoped 上下文里（返回的实例被跟踪）。
    /// </summary>
    /// <remarks>
    /// 工站页把这个实例直接绑到编辑表单，再原样交回 <see cref="SaveStationAsync"/> ——
    /// 走的是"被跟踪对象改完一起 SaveChanges"这条路。改成脱离上下文属于写模型（P2）的改造范围，
    /// 混在这次里做会把"编辑保存"整条链路一起改掉，风险不对等。
    /// </remarks>
    public Task<Station?> GetStationAsync(int id, CancellationToken cancellationToken = default)
        => _db.Stations
            .Include(x => x.Positions)
            .Include(x => x.Tags)
            .Include(x => x.Curves).ThenInclude(c => c.Series)
            .Include(x => x.Curves).ThenInclude(c => c.Criteria)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task SaveStationAsync(Station station, CancellationToken cancellationToken = default)
    {
        station.Code = station.Code.Trim();
        station.Name = station.Name.Trim();
        station.DataFilePath = (station.DataFilePath ?? "").Trim();
        SyncPositions(station);
        await EnsureStationInvariantsAsync(station, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// 工站级的硬约束：编码唯一、握手参数可用、以及整条线的拓扑自洽。
    /// </summary>
    /// <remarks>
    /// 界面也做同样的校验，但那只是 UI：这些规则一旦被绕过（历史脏配置、脚本直接写库），
    /// 后果都落在采集端，而且表现形式很难往"配置错了"上想 ——
    /// 触发值与回写码相同会让工站被无限重复触发；一条线没有末站则托盘会话永不关闭、MES 从不上报。
    /// </remarks>
    private async Task EnsureStationInvariantsAsync(Station station, CancellationToken cancellationToken)
    {
        if (station.Code.Length == 0)
        {
            throw new InvalidOperationException("工站编码不能为空");
        }

        if (StationConfigLimits.TriggerValueError(station.TriggerValue) is { } triggerError)
        {
            throw new InvalidOperationException($"工站 {station.Code} 的{triggerError}");
        }

        if (StationConfigLimits.PalletCodeLengthError(station.PalletCodeLength) is { } lengthError)
        {
            throw new InvalidOperationException($"工站 {station.Code} 的{lengthError}");
        }

        // 文件源点位必须有一个可读的文件：采集侧遇到"有文件源点位但路径为空"每次都失败，
        // 与其让现场对着结果码 9 排查，不如在保存工站这一步就说清楚。
        // 只校验"有文件源点位时非空"，不校验存在性：文件由设备写，配置时它完全可以还没出现。
        if (station.DataFilePath.Length == 0)
        {
            var fileSourceTag = await _db.Tags.AsNoTracking()
                .Where(x => x.StationId == station.Id && x.Source == TagDataSource.JsonFile)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (fileSourceTag is not null)
            {
                throw new InvalidOperationException(
                    $"工站 {station.Code} 的点位 {fileSourceTag} 取自数据文件，但「数据文件路径」为空");
            }
        }

        if (await _db.Stations.AnyAsync(
                x => x.Id != station.Id && x.Code.ToLower() == station.Code.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"工站编码「{station.Code}」已存在");
        }

        // 顺序与首末站只按"启用的工站"判定：停用的工站不参与采集，
        // 也就不该挡住维护期间的调整（例如临时停掉唯一的末站）。
        var others = await _db.Stations.AsNoTracking()
            .Where(x => x.Id != station.Id && x.Enabled)
            .Select(x => new { x.Code, x.Sequence, x.IsFirstStation, x.IsLastStation })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!station.Enabled)
        {
            return;
        }

        if (station.Sequence <= 0)
        {
            throw new InvalidOperationException($"工站 {station.Code} 的产线顺序必须大于 0");
        }

        if (others.FirstOrDefault(x => x.Sequence == station.Sequence) is { } sameOrder)
        {
            throw new InvalidOperationException($"产线顺序 {station.Sequence} 已被工站 {sameOrder.Code} 占用");
        }

        if (station.IsFirstStation && others.FirstOrDefault(x => x.IsFirstStation) is { } first)
        {
            throw new InvalidOperationException($"工站 {first.Code} 已是首站；一条线只能有一个启用的首站");
        }

        if (station.IsLastStation && others.FirstOrDefault(x => x.IsLastStation) is { } last)
        {
            throw new InvalidOperationException($"工站 {last.Code} 已是末站；一条线只能有一个启用的末站");
        }
    }

    public async Task DeleteStationAsync(int id, CancellationToken cancellationToken = default)
    {
        var item = await _db.Stations.FindAsync([id], cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        // 点位随工站级联删除，它们的型号覆盖行必须一起清掉（与 DeleteTagAsync 同一套做法）：
        // 留下的悬空行在限值矩阵里看不见，却会被"覆盖点位 N 个"继续算进去。
        var tagIds = await _db.Tags.AsNoTracking()
            .Where(x => x.StationId == id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (tagIds.Count > 0)
        {
            var limits = await _db.RecipeLimits
                .Where(x => tagIds.Contains(x.TagId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            _db.RecipeLimits.RemoveRange(limits);
        }

        _db.Stations.Remove(item);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BumpVersionAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveTagAsync(TagDefinition tag, CancellationToken cancellationToken = default)
    {
        tag.Name = (tag.Name ?? "").Trim();
        if (tag.Name.Length == 0)
        {
            throw new InvalidOperationException("点位名称不能为空");
        }

        // 点位一律属于这一件产品。工站级（0）已取消：空位不读，超限记在这一件上。
        tag.PositionIndex = 1;

        // 库里 (StationId, Name) 上有唯一索引，重名会抛出 SQLite 的原始错误；
        // 先查出来，用户看到的才是"哪个点位重名"（与型号/PLC 同一套做法）。
        if (await _db.Tags.AnyAsync(
                x => x.StationId == tag.StationId && x.Id != tag.Id && x.Name.ToLower() == tag.Name.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"点位名称「{tag.Name}」在该工站下已存在");
        }

        // 对话框里也校验这一条，但那只是 UI：从 MES 或其它入口直接写库照样能留下自相矛盾的限值，
        // 而采集端只会默默把黄线收敛进红线，明细页上显示的预警限就跟实际判据不是一回事了。
        if (TagLimits.From(tag).ConsistencyError() is { } error)
        {
            throw new InvalidOperationException($"点位 {tag.Name} 的限值互相矛盾：{error}");
        }

        // 文件源点位没有可读的路径：这类配置在采集侧表现为"每次触发都失败（结果码 9）"，
        // 与其让现场对着结果码排查，不如在保存点位这一步就说清楚。
        // 只校验非空，不校验存在性：文件由设备写，配置时它完全可以还没出现。
        if (tag.Source == TagDataSource.JsonFile)
        {
            var station = await _db.Stations.AsNoTracking()
                .Where(x => x.Id == tag.StationId)
                .Select(x => new { x.Code, x.DataFilePath })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (station is null)
            {
                throw new InvalidOperationException($"点位 {tag.Name} 所属的工站不存在，无法保存");
            }

            if (string.IsNullOrWhiteSpace(station.DataFilePath))
            {
                throw new InvalidOperationException(
                    $"点位 {tag.Name} 取自数据文件，但工站 {station.Code} 还没配「数据文件路径」，请先到「点位」页签顶部填写或选择");
            }
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
        curve.Code = curve.Code.Trim();
        if (curve.Code.Length == 0)
        {
            throw new InvalidOperationException("曲线编码不能为空");
        }

        if (StationConfigLimits.PointCountError(curve.PointCount) is { } pointError)
        {
            throw new InvalidOperationException($"曲线 {curve.Code} 的{pointError}");
        }

        curve.PositionIndex = 1;

        // (StationId, Code) 上事实上唯一：PositionIndex 恒为 1，按主键之外的重复编码先给出中文提示。
        if (await _db.Curves.AnyAsync(
                x => x.StationId == curve.StationId && x.Id != curve.Id && x.Code.ToLower() == curve.Code.ToLower(),
                cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"曲线编码「{curve.Code}」在该工站下已存在");
        }

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

        // 界面也校验编码，但那只是 UI：从 MES 或脚本直接写库照样能落一条空编码/带逗号的型号。
        // 空编码的型号被设为当前后，记录里的 RecipeCode 是空串，在报表里与"未选型号"再也分不开。
        if (RecipeCodeRules.Error(recipe.Code) is { } codeError)
        {
            throw new InvalidOperationException(codeError);
        }

        // 名称留空时回落为编码：列表与下拉里空名称就是一格空白，比编码还难认。
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
    }

    /// <summary>
    /// 只保存型号的限值覆盖行（传入集合即最终状态），不碰名称/启用状态/备注。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SaveRecipeAsync"/> 分开，是为了让限值编辑器不必把快照里的整份型号写回去：
    /// 那份快照可能是几分钟前读的，另一会话刚把这个型号停用/改名，一保存就会把旧值盖回去
    /// （甚至把已停用的型号重新启用，而当前型号指针早被清空，状态看上去毫无异常）。
    /// </remarks>
    public async Task SaveRecipeLimitsAsync(int recipeId, IReadOnlyList<RecipeLimit> limits, CancellationToken cancellationToken = default)
    {
        var recipe = await _db.Recipes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == recipeId, cancellationToken)
            .ConfigureAwait(false);
        if (recipe is null)
        {
            throw new InvalidOperationException($"型号 {recipeId} 不存在");
        }

        await EnsureRecipeLimitsConsistentAsync(recipe.Code, limits, cancellationToken).ConfigureAwait(false);
        await SyncLimitsAsync(recipeId, limits, cancellationToken).ConfigureAwait(false);
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
            _db.SystemSettings.Update(settings);
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
            // 有料地址要原样保留：采集端仍按它做空位判定，
            // 这里清掉就等于"点一次保存，空位检测悄悄失效"。
            if (string.IsNullOrWhiteSpace(keep.Name) || keep.Name.StartsWith("产品位", StringComparison.Ordinal))
            {
                keep.Name = "产品";
            }
        }

        station.Positions.Add(keep);
    }
}
