using DataTrace.Application.Alarms;
using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Reporting;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
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

internal static class ConfigMigrationTestSupport
{
    public static async Task RewindMesOutboxDeliveryStateAsync(ConfigDbContext db)
    {
        // 语义：把用当前模型 EnsureCreated 出来的库退回「baseline 之后、后续迁移之前」的老结构。
        // 之后每加一个配置库迁移，这里都要把对应的列/索引退回去，否则迁移重放会撞 duplicate column。
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_MesOutbox_EventId\"");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_MesOutbox_Status_NextAttemptAt_CreatedAt\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"MesOutbox\" DROP COLUMN \"EventId\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"MesOutbox\" DROP COLUMN \"NextAttemptAt\"");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX \"IX_MesOutbox_Status_CreatedAt\" ON \"MesOutbox\" (\"Status\", \"CreatedAt\")");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"ActiveSessions\" DROP COLUMN \"RecipeSnapshot\"");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"ConfigurationRevisions\"");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Stations DROP COLUMN EventIdAddress");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Stations DROP COLUMN ConfirmationRequestAddress");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Stations DROP COLUMN ConfirmationAckAddress");
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

    public static async Task<CollectHarness> CreateAsync(bool configureMes = false, Action<IServiceCollection>? configureServices = null)
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
        // 采集侧运行时观测：与 AddDataTraceCollector 里的注册保持一致。
        services.AddSingleton<ICollectorDiagnostics, CollectorDiagnostics>();
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();

        using (var seedScope = provider.CreateScope())
        {
            await seedScope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
            await ConfigureGenericTestStationAsync(
                seedScope.ServiceProvider.GetRequiredService<ConfigDbContext>());
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

    private static async Task ConfigureGenericTestStationAsync(ConfigDbContext db)
    {
        var extraDemoStations = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
            .Where(item => item.Code == "OP40" || item.Code == "OP50" || item.Code == "OP70")
            .ToListAsync();
        if (extraDemoStations.Count > 0)
        {
            var tagIds = extraDemoStations.SelectMany(station => station.Tags).Select(tag => tag.Id).ToArray();
            var limits = await db.RecipeLimits
                .Where(limit => tagIds.Contains(limit.TagId))
                .ToListAsync();
            db.RecipeLimits.RemoveRange(limits);
            db.Tags.RemoveRange(extraDemoStations.SelectMany(station => station.Tags));
            db.Curves.RemoveRange(extraDemoStations.SelectMany(station => station.Curves));
            db.Stations.RemoveRange(extraDemoStations);
        }

        var stations = await db.Stations
            .Include(item => item.Tags)
            .Include(item => item.Curves)
            .Where(item => item.Code == "OP130" || item.Code == "OP140" || item.Code == "OP150" || item.Code == "OP20" || item.Code == "OP30")
            .ToListAsync();
        if (stations.Count == 0)
        {
            return;
        }

        var restoredStations = new List<Station>();
        foreach (var station in stations)
        {
            var (code, name, trigger, pallet, temperature, pressure, curveY, curveX, sequence) = station.Code switch
            {
                "OP130" => ("ST010", "上料工站", "D1000", "D1010", "D1110", "D1100", "D2200", "D2400", 10),
                "OP140" => ("ST020", "压装工站", "D1200", "D1210", "D1310", "D1300", "D3600", "D4000", 20),
                "OP150" => ("ST030", "下线工站", "D1400", "D1410", "D1510", "D1500", "D5200", "D5600", 30),
                "OP20" => ("ST050", "性能检测工站", "D1800", "D1810", "D1910", "D1900", "D8400", "D8800", 50),
                "OP30" => ("ST060", "终检工站", "D2000", "D2010", "D2110", "D2100", "D10000", "D10400", 60),
                _ => throw new InvalidOperationException($"未识别的演示站点：{station.Code}")
            };
            var oldTagIds = station.Tags.Select(tag => tag.Id).ToArray();
            var oldLimits = await db.RecipeLimits
                .Where(limit => oldTagIds.Contains(limit.TagId))
                .ToListAsync();
            db.RecipeLimits.RemoveRange(oldLimits);
            db.Tags.RemoveRange(station.Tags);
            db.Curves.RemoveRange(station.Curves);

            station.Code = code;
            station.Name = name;
            station.Sequence = sequence;
            station.IsFirstStation = sequence == 10;
            station.IsLastStation = sequence == 60;
            station.TriggerAddress = trigger;
            station.TriggerValue = 1;
            station.EventIdAddress = "";
            station.ConfirmationRequestAddress = "";
            station.ConfirmationAckAddress = "";
            station.PalletCodeAddress = pallet;
            station.PalletCodeLength = 16;
            station.PalletCodeDataType = PlcDataType.String;
            station.Tags =
            [
                new TagDefinition
                {
                    Name = "工站温度",
                    Address = temperature,
                    DataType = PlcDataType.Float,
                    Unit = "℃",
                    LowerLimit = 0,
                    UpperLimit = 80,
                    WarningUpperLimit = 60,
                    TargetValue = 40,
                    PositionIndex = 1
                },
                new TagDefinition
                {
                    Name = "压力",
                    Address = pressure,
                    DataType = PlcDataType.Float,
                    Unit = "kN",
                    LowerLimit = 5,
                    UpperLimit = 20,
                    WarningLowerLimit = 6,
                    WarningUpperLimit = 16,
                    TargetValue = 12.5,
                    PositionIndex = 1
                }
            ];
            station.Curves =
            [
                new CurveDefinition
                {
                    Code = station.Code + "_PD",
                    Name = "位移压力曲线",
                    PointCount = 50,
                    PositionIndex = 1,
                    Enabled = true,
                    Series =
                    [
                        new CurveSeries
                        {
                            Name = "压力",
                            Role = SeriesRole.Y,
                            StartAddress = curveY,
                            DataType = PlcDataType.Float,
                            StrideWords = 2,
                            Unit = "kN"
                        },
                        new CurveSeries
                        {
                            Name = "位移",
                            Role = SeriesRole.X,
                            StartAddress = curveX,
                            DataType = PlcDataType.Float,
                            StrideWords = 2,
                            Unit = "mm"
                        }
                    ]
                }
            ];
            restoredStations.Add(station);
        }

        var version = await db.ConfigVersions.FirstOrDefaultAsync();
        if (version is not null)
        {
            version.Version++;
        }

        await db.SaveChangesAsync();

        var recipe = await db.Recipes
            .Include(item => item.Limits)
            .SingleOrDefaultAsync(item => item.Code == "A100");
        if (recipe is not null)
        {
            foreach (var station in restoredStations)
            {
                recipe.Limits.Add(new RecipeLimit
                {
                    TagId = station.Tags.Single(tag => tag.Name == "压力").Id,
                    UpperLimit = 16
                });
                recipe.Limits.Add(new RecipeLimit
                {
                    TagId = station.Tags.Single(tag => tag.Name == "工站温度").Id,
                    WarningUpperLimit = 45
                });
            }

            await db.SaveChangesAsync();
        }
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

    public Task RepairPostCommitStateAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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

    public Task<bool> ExistsBySerialAsync(string monthKey, string serialNo, CancellationToken cancellationToken = default)
        => Task.FromResult(Records.Any(x =>
            x.MonthKey == monthKey && string.Equals(x.Record.SerialNo, serialNo, StringComparison.Ordinal)));

    public Task<bool> ExistsCollectAsync(string monthKey, string serialNo, int stationId, DateTime triggerTime, CancellationToken cancellationToken = default)
        => Task.FromResult(Records.Any(x =>
            x.MonthKey == monthKey && string.Equals(x.Record.SerialNo, serialNo, StringComparison.Ordinal)
            && x.Record.StationId == stationId && x.Record.TriggerTime == triggerTime));

    public Task<long?> FindCollectRecordIdAsync(string monthKey, string serialNo, int stationId, DateTime triggerTime, CancellationToken cancellationToken = default)
        => Task.FromResult(Records
            .Where(x => x.MonthKey == monthKey && string.Equals(x.Record.SerialNo, serialNo, StringComparison.Ordinal)
                        && x.Record.StationId == stationId && x.Record.TriggerTime == triggerTime)
            .Select(x => (long?)x.Record.Id)
            .FirstOrDefault());

    public Task<DataTrace.Domain.Entities.PalletSession?> GetSessionAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<DataTrace.Domain.Entities.PalletSession?>(null);

    public Task<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>> GetSessionRecordsAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>>(
            Records.Where(x => x.MonthKey == monthKey && x.Record.PalletSessionId == sessionId).Select(x => x.Record).ToList());

    public Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySerialNoAsync(
        string serialNo,
        CancellationToken cancellationToken = default)
        => FindSessionTracesAsync(serialNo, null);

    public Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySessionIdAsync(
        long sessionId,
        CancellationToken cancellationToken = default)
        => FindSessionTracesAsync(null, sessionId);

    private Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesAsync(
        string? serialNo,
        long? sessionId)
    {
        var matches = Records
            .Where(x => sessionId is not null ? x.Record.PalletSessionId == sessionId : string.Equals(x.Record.SerialNo, serialNo, StringComparison.Ordinal))
            .ToList();
        var traces = matches
            .Where(x => x.Record.PalletSessionId > 0 && x.Record.PalletSession is not null)
            .GroupBy(x => (x.MonthKey, x.Record.PalletSessionId))
            .Select(group => new CollectSessionTrace
            {
                MonthKey = group.Key.MonthKey,
                SessionId = group.Key.PalletSessionId,
                Session = group.First().Record.PalletSession,
                Records = group.Select(x => x.Record).ToList()
            })
            .ToList();
        traces.AddRange(matches
            .Where(x => x.Record.PalletSessionId <= 0 || x.Record.PalletSession is null)
            .Select(x => new CollectSessionTrace
            {
                MonthKey = x.MonthKey,
                SessionId = null,
                Session = null,
                Records = [x.Record]
            }));
        return Task.FromResult<IReadOnlyList<CollectSessionTrace>>(
            traces.OrderByDescending(x => x.Session?.StartTime ?? x.Records[0].TriggerTime).ToList());
    }

    public Task<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>> QueryForReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DataTrace.Domain.Entities.CollectRecord>>(
            Records.Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to).Select(x => x.Record).ToList());

    public Task<IReadOnlyList<StationJudgementMark>> ListMonthJudgementsAsync(string monthKey, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StationJudgementMark>>(
            Records.Where(x => x.MonthKey == monthKey)
                .Select(x => new StationJudgementMark
                {
                    StationId = x.Record.StationId,
                    StationCode = x.Record.StationCode,
                    CompleteTime = x.Record.CompleteTime,
                    Judgement = x.Record.Judgement,
                    ResultCode = x.Record.ResultCode
                })
                .ToList());

    public Task<IReadOnlyList<TagObservation>> ListRecentTagObservationsAsync(
        string monthKey,
        DateTime since,
        int perTag,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TagObservation>>([]);

    public Task<IReadOnlyList<JudgementCount>> CountJudgementsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<JudgementCount>>(
            Records
                .Where(x => x.Record.TriggerTime >= from && x.Record.TriggerTime <= to)
                .Where(x => stationId is null || x.Record.StationId == stationId)
                .Where(x => recipeCode is null || x.Record.RecipeCode == recipeCode)
                .GroupBy(x => new { Day = x.Record.TriggerTime.Date, Hour = x.Record.TriggerTime.Hour, x.Record.RecipeCode, x.Record.Judgement })
                .Select(g => new JudgementCount
                {
                    Day = g.Key.Day,
                    Hour = g.Key.Hour,
                    RecipeCode = g.Key.RecipeCode ?? "",
                    Judgement = g.Key.Judgement,
                    Count = g.Count()
                })
                .ToList());

    public List<StationPass> StationPasses { get; } = [];

    public Task<IReadOnlyList<StationPass>> ListStationPassesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<StationPass>>(
            StationPasses
                .Where(pass => pass.CompleteTime >= from && pass.CompleteTime <= to)
                .ToList());

    public List<FinishedPieceObservation> FinishedPieces { get; } = [];

    public Task<IReadOnlyList<FinishedPieceObservation>> ListFinishedPiecesAsync(DateTime from, DateTime to, string? recipeCode = null, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FinishedPieceObservation>>(
            FinishedPieces
                .Where(piece => piece.EndTime >= from && piece.EndTime <= to)
                .Where(piece => recipeCode is null || piece.RecipeCode == recipeCode)
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
                .Select(t => new TagIssuePoint { TagName = t.TagName, Missing = t.NumericValue is null })
                .ToList());

    public List<InSpecReading> InSpecReadings { get; } = [];

    public Task<IReadOnlyList<InSpecReading>> ListInSpecReadingsAsync(
        DateTime from,
        DateTime to,
        string stationCode,
        string tagName,
        int? stationId,
        string? recipeCode = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<InSpecReading>>(InSpecReadings);

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

    public IReadOnlyList<string> ListMonthKeys()
        => Records.Select(x => x.MonthKey).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

    public Task<IReadOnlyList<string>> GetDeletionBlockersAsync(
        string monthKey, DateTime cutoff, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<(int Curves, int Archives)> GetExternalFileReferenceCountsAsync(
        string fileMonthKey, string excludingRuntimeMonthKey, CancellationToken cancellationToken = default)
        => Task.FromResult((0, 0));

    public Task<IReadOnlyList<string>> GetExternalFileMonthKeysAsync(
        string runtimeMonthKey, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>([]);

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
        CancellationToken cancellationToken = default,
        DataTrace.Domain.Enums.Judgement? judgement = null)
    {
        if (take <= 0)
        {
            return Task.FromResult<IReadOnlyList<CurveFeaturePoint>>([]);
        }

        var points = Saved
            .Where(r => r.Record.TriggerTime >= from && r.Record.TriggerTime <= to)
            // 型号过滤与真身同语义：先过滤再 take（"最新 N 条"针对的是筛出来的那批）。
            .Where(r => recipeCodes is null || recipeCodes.Count == 0 || recipeCodes.Contains(r.Record.RecipeCode))
            .Where(r => judgement is null || r.Record.Judgement == judgement)
            .SelectMany(r => r.Curves
                .Where(c => c.Record.CurveDefinitionId == curveDefinitionId)
                .SelectMany(c => c.Features
                    .Where(f => seriesName is null || f.SeriesName == seriesName)
                    .Select(f => new CurveFeaturePoint
                    {
                        CurveRecordId = c.Record.Id,
                        Time = r.Record.TriggerTime,
                        PalletCode = r.Record.PalletCode,
                        ActualJudgement = r.Record.Judgement,
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
                    g.Count(x => x.r.Record.Judgement == DataTrace.Domain.Enums.Judgement.Ng),
                    g.Count(x => x.r.Record.Judgement == DataTrace.Domain.Enums.Judgement.None)))
                .OrderByDescending(x => x.Total)
                .ThenBy(x => x.RecipeCode, StringComparer.Ordinal)
                .ToList());
}
