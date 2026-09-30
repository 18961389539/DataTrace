using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Evaluation;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Domain.Validation;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Infrastructure.Persistence;

// 按聚合根拆成 partial 多文件：快照与事务本文件，PLC / 工站 / 曲线 / 型号 / 设置各一份。
public sealed partial class ConfigRepository : IConfigRepository
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

    /// <summary>
    /// 把一段配置库写入包进一个事务。
    /// </summary>
    /// <remarks>
    /// 必须落在 scoped 的 <see cref="_db"/> 上：审计写入用的 <c>AuditLogger</c> 注入的是同一个实例，
    /// 只有同一个上下文/连接上的操作才会跟着一起回滚（读路径走工厂，与这里无关）。
    /// 工作块抛异常时事务在 Dispose 时回滚，库里不会留下半笔改动。
    /// </remarks>
    public async Task<T> InTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await work(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

