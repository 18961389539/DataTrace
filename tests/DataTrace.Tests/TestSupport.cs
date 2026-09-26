using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Seeding;
using DataTrace.Plc;
using DataTrace.Plc.Queue;
using DataTrace.Plc.Simulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DataTrace.Tests;

/// <summary>
/// 临时工作目录：每个测试独占 root，测试结束尽力清理（SQLite 文件可能仍被句柄占用，忽略失败）。
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "datatrace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>拼出工作目录下的路径（R 为根目录，segments 为子路径片段）。</summary>
    public string Path(params string[] segments)
    {
        var parts = new string[segments.Length + 1];
        parts[0] = Root;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return System.IO.Path.Combine(parts);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 句柄未释放时不强求清理
        }
    }
}

/// <summary>直接构造独立的挂起配置库（不经过 DI），供持久化层单测使用。</summary>
internal static class TestDatabase
{
    public static DbContextOptions<ConfigDbContext> ConfigOptions(string databasePath)
        => new DbContextOptionsBuilder<ConfigDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;

    public static async Task<ConfigDbContext> CreateConfigAsync(string databasePath)
    {
        var db = new ConfigDbContext(ConfigOptions(databasePath));
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        return db;
    }

    /// <summary>
    /// 与 <paramref name="db"/> 指向同一个库文件的"每查询一个新 context"工厂。
    /// 配置仓储的只读路径走它，所以单测构造仓储时也得给一个，否则读到的是另一套（空）库。
    /// </summary>
    public static IDbContextFactory<ConfigDbContext> FactoryFor(ConfigDbContext db)
    {
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("测试库没有连接串");
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connectionString).Options;
        return new TestConfigDbContextFactory(options);
    }
}

/// <summary>测试用的上下文工厂：直接按给定 options 造 context，不走 DI 池化。</summary>
internal sealed class TestConfigDbContextFactory(DbContextOptions<ConfigDbContext> options)
    : IDbContextFactory<ConfigDbContext>
{
    public ConfigDbContext CreateDbContext() => new(options);
}

/// <summary>成套启动一个完整的采集环境：配置库 + 运行库 + 模拟 PLC + 采集流水线。</summary>
internal sealed class CollectHarness : IAsyncDisposable
{
    private readonly TempWorkspace _workspace;

    private CollectHarness(TempWorkspace workspace, ServiceProvider provider, CollectingLoggerProvider logs)
    {
        _workspace = workspace;
        Provider = provider;
        Logs = logs;
    }

    public string Root => _workspace.Root;

    public ServiceProvider Provider { get; }

    /// <summary>内存日志：用来断言采集侧对坏配置给出了明确说明，而不是静默跳过。</summary>
    public CollectingLoggerProvider Logs { get; }

    public AppConfigurationSnapshot Snapshot { get; private set; } = new();

    public PlcConnection Plc { get; private set; } = new();

    public IReadOnlyList<DataTrace.Domain.Entities.Station> Stations { get; private set; } = [];

    public InMemoryPlcDriver Simulator { get; private set; } = null!;

    public StationCollectPipeline Pipeline { get; private set; } = null!;

    public PlcRequestQueue Queue { get; private set; } = null!;

    /// <summary>常驻作用域，用于解析 IRuntimeStore / IConfigRepository 等 Scoped 服务。</summary>
    public IServiceScope Scope { get; private set; } = null!;

    public static async Task<CollectHarness> CreateAsync(bool configureMes = false)
    {
        var workspace = new TempWorkspace();
        var root = workspace.Root;
        var logs = new CollectingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddDebug();
            b.AddProvider(logs);
        });
        services.AddDataTraceInfrastructure(root);
        services.AddDataTracePlc();
        services.AddSingleton<StationCollectPipeline>();
        var provider = services.BuildServiceProvider();

        using (var seedScope = provider.CreateScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
        }

        var harness = new CollectHarness(workspace, provider, logs);
        harness.Scope = provider.CreateScope();
        harness.Snapshot = await harness.Scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetSnapshotAsync();
        harness.Plc = harness.Snapshot.PlcConnections.Single();
        harness.Stations = harness.Snapshot.Stations.OrderBy(s => s.Sequence).ToList();
        harness.Simulator = provider.GetRequiredService<SimulatorCatalog>().Get(harness.Plc.Id);
        await harness.Simulator.ConnectAsync();
        harness.Pipeline = provider.GetRequiredService<StationCollectPipeline>();
        harness.Queue = new PlcRequestQueue(harness.Simulator, ownsDriver: false);

        if (configureMes)
        {
            var repo = harness.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            // 快照是共享只读实例，改前先克隆。
            var settings = harness.Snapshot.Settings.Clone();
            settings.MesEnabled = true;
            settings.MesEndpoint = "http://localhost/mes";
            await repo.SaveSettingsAsync(settings);
            harness.Snapshot = await repo.GetSnapshotAsync();
        }

        return harness;
    }

    public IRuntimeStore RuntimeStore => Scope.ServiceProvider.GetRequiredService<IRuntimeStore>();

    public IConfigRepository ConfigRepository => Scope.ServiceProvider.GetRequiredService<IConfigRepository>();

    public DataTrace.Application.Realtime.IRuntimeStatusHub StatusHub =>
        Provider.GetRequiredService<DataTrace.Application.Realtime.IRuntimeStatusHub>();

    public DataTrace.Domain.Entities.Station Station(int index) => Stations[index];

    /// <summary>执行一次采集并返回写回 PLC 的响应码。</summary>
    public async Task<short> RunAsync(DataTrace.Domain.Entities.Station station)
    {
        await Pipeline.ExecuteAsync(station, Plc, Snapshot, Queue, CancellationToken.None);
        return (short)Simulator.GetWord(station.TriggerAddress);
    }

    public async Task<CollectQueryResult> QueryAsync(string palletCode)
        => await RuntimeStore.QueryAsync(new CollectQueryRequest
        {
            From = DateTime.Today.AddDays(-2),
            To = DateTime.Today.AddDays(2),
            PalletCode = palletCode,
            Take = 50
        });

    /// <summary>刷新配置快照（改了设置后重新读取）。</summary>
    public async Task RefreshSnapshotAsync()
        => Snapshot = await ConfigRepository.GetSnapshotAsync();

    public async ValueTask DisposeAsync()
    {
        Scope.Dispose();
        await Queue.DisposeAsync();
        await Provider.DisposeAsync();
        _workspace.Dispose();
    }
}

/// <summary>内存版运行库，用于报表/统计类纯逻辑测试，避免落盘开销。</summary>
internal class FakeRuntimeStore : IRuntimeStore
{
    public List<(string MonthKey, DataTrace.Domain.Entities.CollectRecord Record)> Records { get; } = [];

    public List<CollectSaveRequest> Saved { get; } = [];

    public List<string> DeletedMonths { get; } = [];

    public virtual Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
    {
        Saved.Add(request);
        Records.Add((request.MonthKey, request.Record));
        return Task.CompletedTask;
    }

    public Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default)
    {
        var items = Records
            .Where(x => x.Record.TriggerTime >= request.From && x.Record.TriggerTime <= request.To)
            .Select(x => new CollectRecordListItem { MonthKey = x.MonthKey, Record = x.Record })
            .ToList();
        return Task.FromResult(new CollectQueryResult { Total = items.Count, Items = items });
    }

    public Task<DataTrace.Domain.Entities.CollectRecord?> GetRecordAsync(string monthKey, long recordId, CancellationToken cancellationToken = default)
        => Task.FromResult<DataTrace.Domain.Entities.CollectRecord?>(
            Records.FirstOrDefault(x => x.MonthKey == monthKey && x.Record.Id == recordId).Record);

    public Task<DataTrace.Domain.Entities.PalletSession?> GetSessionAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<DataTrace.Domain.Entities.PalletSession?>(null);

    public Task<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>> GetSessionRecordsAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>>(
            Records.Where(x => x.MonthKey == monthKey && x.Record.PalletSessionId == sessionId).Select(x => x.Record).ToList());

    public Task<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>> QueryForReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>>(
            Records.Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to).Select(x => x.Record).ToList());

    public Task<IReadOnlyList<JudgementCount>> CountJudgementsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<JudgementCount>>(
            Records
                .Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to)
                .Where(x => stationId is null || x.Record.StationId == stationId)
                .Where(x => recipeCode is null || x.Record.RecipeCode == recipeCode)
                .GroupBy(x => new { Day = x.Record.TriggerTime.Date, x.Record.RecipeCode, x.Record.Judgement })
                .Select(g => new JudgementCount
                {
                    Day = g.Key.Day,
                    RecipeCode = g.Key.RecipeCode ?? "",
                    Judgement = g.Key.Judgement,
                    Count = g.Count()
                })
                .ToList());

    public Task<IReadOnlyList<string>> ListRecipeCodesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            Records
                .Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to)
                .Select(x => x.Record.RecipeCode)
                .Distinct()
                .ToList());

    public Task<IReadOnlyList<TagIssuePoint>> QueryOutOfLimitTagsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TagIssuePoint>>(
            Records
                .Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to)
                .Where(x => stationId is null || x.Record.StationId == stationId)
                .Where(x => recipeCode is null || x.Record.RecipeCode == recipeCode)
                .SelectMany(x => x.Record.TagValues.Where(t => t.IsOutOfLimit))
                .Select(t => new TagIssuePoint { TagName = t.TagName })
                .ToList());

    public Task<IReadOnlyList<TagIssuePoint>> QueryWarningTagsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TagIssuePoint>>(
            Records
                .Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to)
                .Where(x => stationId is null || x.Record.StationId == stationId)
                .Where(x => recipeCode is null || x.Record.RecipeCode == recipeCode)
                .SelectMany(x => x.Record.TagValues.Where(t => t.IsWarning))
                .Select(t => new TagIssuePoint { TagName = t.TagName })
                .ToList());

    public Task<IReadOnlyList<TagTrendPoint>> QueryTagTrendAsync(DateTime from, DateTime to, int tagId, string? recipeCode = null, int take = 0, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TagTrendPoint>>(
            Records
                .Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to)
                .Where(x => recipeCode is null || x.Record.RecipeCode == recipeCode)
                .SelectMany(x => x.Record.TagValues
                    .Where(t => t.TagId == tagId && t.NumericValue is not null)
                    .Select(t => new TagTrendPoint
                    {
                        Time = x.Record.TriggerTime,
                        Value = t.NumericValue!.Value,
                        PalletCode = x.Record.PalletCode,
                        LowerLimit = t.LowerLimit,
                        UpperLimit = t.UpperLimit
                    }))
                // 与真身同语义：take 取的是区间内"最新"的 N 点，返回时升序。
                .OrderByDescending(p => p.Time)
                .Take(take > 0 ? take : int.MaxValue)
                .OrderBy(p => p.Time)
                .ToList());

    public Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default)
    {
        foreach (var (_, record) in Records.Where(x => x.MonthKey == monthKey && x.Record.PalletSessionId == sessionId))
        {
            record.Judgement = DataTrace.Domain.Enums.Judgement.Ng;
        }

        return Task.CompletedTask;
    }

    public Task DeleteMonthAsync(string monthKey, CancellationToken cancellationToken = default)
    {
        DeletedMonths.Add(monthKey);
        Records.RemoveAll(x => x.MonthKey == monthKey);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 波形特征不在 CollectRecord 上（曲线是独立实体），因此从保存请求里的 Curves 集合取。
    /// 时间与判定沿用它所属的采集记录，与真实月库的 join 语义一致。
    /// </summary>
    public Task<IReadOnlyList<CurveFeaturePoint>> QueryCurveFeaturesAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int take,
        IReadOnlyCollection<string>? recipeCodes = null,
        CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return Task.FromResult<IReadOnlyList<CurveFeaturePoint>>([]);
        }

        var points = Saved
            .Where(r => r.Record.TriggerTime >= from && r.Record.TriggerTime <= to)
            // 型号过滤与真身同语义：先过滤再 take（"最新 N 条"针对的是筛出来的那批）。
            .Where(r => recipeCodes is null || recipeCodes.Count == 0 || recipeCodes.Contains(r.Record.RecipeCode))
            .SelectMany(r => r.Curves
                .Where(c => c.Record.CurveDefinitionId == curveDefinitionId)
                .SelectMany(c => c.Features
                    .Where(f => seriesName is null || f.SeriesName == seriesName)
                    .Select(f => new CurveFeaturePoint
                    {
                        CurveRecordId = c.Record.Id,
                        Time = r.Record.TriggerTime,
                        PalletCode = r.Record.PalletCode,
                        IsNg = r.Record.Judgement == DataTrace.Domain.Enums.Judgement.Ng,
                        RecipeCode = r.Record.RecipeCode,
                        SeriesName = f.SeriesName,
                        Role = f.Role,
                        Feature = f
                    })))
            .OrderByDescending(p => p.Time)
            .Take(take)
            .OrderBy(p => p.Time)
            .ToList();

        return Task.FromResult<IReadOnlyList<CurveFeaturePoint>>(points);
    }

    public Task<IReadOnlyList<CurveRecipeSampleCount>> CountCurveFeaturesByRecipeAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<CurveRecipeSampleCount>>(
            Saved
                .Where(r => r.Record.TriggerTime >= from && r.Record.TriggerTime <= to)
                .SelectMany(r => r.Curves
                    .Where(c => c.Record.CurveDefinitionId == curveDefinitionId)
                    .SelectMany(c => c.Features
                        .Where(f => seriesName is null || f.SeriesName == seriesName)
                        .Select(f => new { f, r })))
                .GroupBy(x => x.r.Record.RecipeCode ?? "")
                .Select(g => new CurveRecipeSampleCount(
                    g.Key,
                    g.Count(),
                    g.Count(x => x.r.Record.Judgement == DataTrace.Domain.Enums.Judgement.Ng)))
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.RecipeCode, StringComparer.Ordinal)
                .ToList());
}
