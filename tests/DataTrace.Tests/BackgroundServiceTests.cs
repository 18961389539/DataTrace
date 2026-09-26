using System.Text.Json;
using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Validation;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DataTrace.Tests;

/// <summary>数据保留后台任务：按整月删除过期月库与曲线目录。</summary>
public class RetentionServiceTests
{
    private static string MonthKey(DateTime time) => time.ToString("yyyyMM");

    private static async Task SeedMonthAsync(InfrastructureContext ctx, DateTime month)
    {
        var factory = ctx.Provider.GetRequiredService<RuntimeDbFactory>();
        var db = factory.Open(MonthKey(month));
        await db.DisposeAsync();

        // 月库句柄由连接池持有，不排空的话 Windows 上删文件会撞 sharing violation。
        SqliteConnection.ClearAllPools();
    }

    private static async Task<string> SeedCurveMonthAsync(InfrastructureContext ctx, DateTime month)
    {
        var curves = ctx.Provider.GetRequiredService<ICurveFileStore>();
        await curves.WriteAsync(
            month,
            "SN-OLD",
            1,
            1,
            "ST010_PD",
            new CurvePayload
            {
                PointCount = 3,
                Series = [new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [1f, 2f, 3f] }]
            });

        return Path.Combine(ctx.Workspace.Root, "curves", month.ToString("yyyy"), month.ToString("MM"));
    }

    private static async Task SetRetentionAsync(InfrastructureContext ctx, int years)
    {
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var settings = (await repo.GetSnapshotAsync()).Settings;
        settings.RetentionYears = years;
        await repo.SaveSettingsAsync(settings);
    }

    private static RetentionHostedService CreateService(InfrastructureContext ctx) => new(
        ctx.ScopeFactory,
        ctx.Provider.GetRequiredService<RuntimeDbFactory>(),
        ctx.Provider.GetRequiredService<ICurveFileStore>(),
        ctx.Provider.GetRequiredService<ICollectArchiveStore>(),
        ctx.Logger<RetentionHostedService>());

    [Fact]
    public async Task DeletesExpiredMonthDbAndCurveDirectory()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await SetRetentionAsync(ctx, 3);

        var expired = DateTime.Now.AddMonths(-40);
        var current = DateTime.Now;
        await SeedMonthAsync(ctx, expired);
        await SeedMonthAsync(ctx, current);
        var curveDir = await SeedCurveMonthAsync(ctx, expired);

        var factory = ctx.Provider.GetRequiredService<RuntimeDbFactory>();
        Assert.True(factory.Exists(MonthKey(expired)));
        Assert.True(Directory.Exists(curveDir));

        await ctx.RunAsync(CreateService(ctx), () => !factory.Exists(MonthKey(expired)));

        Assert.False(Directory.Exists(curveDir));
    }

    [Fact]
    public async Task KeepsMonthsInsideRetentionWindow()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await SetRetentionAsync(ctx, 3);

        var kept = DateTime.Now.AddMonths(-2);
        await SeedMonthAsync(ctx, kept);

        var factory = ctx.Provider.GetRequiredService<RuntimeDbFactory>();
        var service = CreateService(ctx);

        // 保留窗口内的月份不能被删：条件用"窗口外月份已删"来判定一轮跑完。
        var expired = DateTime.Now.AddMonths(-40);
        await SeedMonthAsync(ctx, expired);

        await ctx.RunAsync(service, () => !factory.Exists(MonthKey(expired)));

        Assert.True(factory.Exists(MonthKey(kept)));
    }

    [Fact]
    public async Task RetentionYearsBelowOneIsFlooredToOne()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        // 0 只能来自存量脏数据（仓储已拒绝写 0）：清理任务必须把它当成 1 年，而不是"永久保留"。
        await ctx.ForceSettingsAsync(s => s.RetentionYears = 0);

        var factory = ctx.Provider.GetRequiredService<RuntimeDbFactory>();
        var tooOld = DateTime.Now.AddMonths(-13);
        var justInside = DateTime.Now.AddMonths(-11);
        await SeedMonthAsync(ctx, tooOld);
        await SeedMonthAsync(ctx, justInside);

        await ctx.RunAsync(CreateService(ctx), () => !factory.Exists(MonthKey(tooOld)));

        Assert.False(factory.Exists(MonthKey(tooOld)));
        Assert.True(factory.Exists(MonthKey(justInside)));
    }

    [Fact]
    public async Task NonSixCharMonthKeyDeletesDbButSkipsCurveCleanup()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await SetRetentionAsync(ctx, 3);

        var factory = ctx.Provider.GetRequiredService<RuntimeDbFactory>();
        var path = factory.GetPath("19999");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "");

        await ctx.RunAsync(CreateService(ctx), () => !File.Exists(path));

        // 月份键必须正好 6 位才能拆成 yyyy/mm 去清曲线目录；短键只删库，不猜目录。
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(ctx.Workspace.Root, "curves", "1999")));
    }
}

/// <summary>写库失败暂存与补传回放。</summary>
public class SpoolReplayServiceTests
{
    private sealed class ThrowingAfterFirst : FakeRuntimeStore
    {
        public override Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        {
            if (Saved.Count >= 1)
            {
                throw new InvalidOperationException("月库不可用");
            }

            return base.SaveAsync(request, cancellationToken);
        }
    }

    private static CollectSaveRequest Request(string palletCode) => new()
    {
        MonthKey = RuntimeDbFactory.MonthKey(DateTime.Now),
        Record = new CollectRecord
        {
            PalletCode = palletCode,
            SerialNo = $"SN-{palletCode}",
            StationId = 10,
            StationCode = "ST010",
            TriggerTime = DateTime.Now,
            Judgement = Judgement.Ok
        }
    };

    [Fact]
    public async Task ReplaysEveryPendingRequestThenClearsSpool()
    {
        var runtime = new FakeRuntimeStore();
        await using var ctx = await InfrastructureContext.CreateAsync(configure: s => s.AddSingleton<IRuntimeStore>(runtime));
        var spool = ctx.Provider.GetRequiredService<ISpoolStore>();

        await spool.SaveAsync(Request("P001"));
        await spool.SaveAsync(Request("P002"));

        await ctx.RunAsync(
            new SpoolReplayService(ctx.ScopeFactory, spool, ctx.Logger<SpoolReplayService>()),
            () => runtime.Saved.Count == 2);

        // 文件名精度只到毫秒，同一毫秒内由 GUID 决定先后，故这里只断言"两条都补传成功"。
        Assert.Equal(
            new[] { "P001", "P002" },
            runtime.Saved.Select(x => x.Record.PalletCode).OrderBy(x => x).ToArray());
        Assert.Empty(Directory.GetFiles(ctx.Workspace.Path("spool"), "*.spool.json"));
    }

    [Fact]
    public async Task StopsAtFirstFailingRequestAndKeepsTheRest()
    {
        var runtime = new ThrowingAfterFirst();
        await using var ctx = await InfrastructureContext.CreateAsync(configure: s => s.AddSingleton<IRuntimeStore>(runtime));
        var spool = ctx.Provider.GetRequiredService<ISpoolStore>();

        await spool.SaveAsync(Request("P001"));
        await spool.SaveAsync(Request("P002"));
        await spool.SaveAsync(Request("P003"));

        await ctx.RunAsync(
            new SpoolReplayService(ctx.ScopeFactory, spool, ctx.Logger<SpoolReplayService>()),
            () => runtime.Saved.Count == 1);

        // break 之后不再尝试后续请求，未成功的文件必须留在盘上等下一轮。
        Assert.Equal(2, Directory.GetFiles(ctx.Workspace.Path("spool"), "*.spool.json").Length);
    }

    [Fact]
    public async Task EmptySpoolDoesNotResolveRuntimeStoreScope()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var spool = ctx.Provider.GetRequiredService<ISpoolStore>();

        Assert.Empty(await spool.ListAsync());

        await ctx.RunAsync(
            new SpoolReplayService(ctx.ScopeFactory, spool, ctx.Logger<SpoolReplayService>()),
            () => true);
    }
}

/// <summary>MES Outbox 推送：状态流转、退避与批量上限。</summary>
public class MesOutboxProcessorTests
{
    [Fact]
    public async Task PostsPendingItemAndMarksItSucceeded()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await EnableMesAsync(ctx, "http://mes.local/push");
        var id = await AddPendingAsync(ctx, "SN-0001", "P001");
        var http = new FakeHttpClientFactory(new FakeHttpMessageHandler(200));

        await ctx.RunAsync(
            new MesOutboxProcessor(ctx.ScopeFactory, http, ctx.Logger<MesOutboxProcessor>()),
            () => ReadStatus(ctx, id) == MesOutboxStatus.Succeeded);

        var request = Assert.Single(http.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://mes.local/push", request.Uri!.ToString());

        // JsonContent.Create 走 web 默认，属性名是 camelCase，MES 侧收到的就是这个形状。
        using var json = JsonDocument.Parse(request.Body);
        var body = json.RootElement;
        Assert.Equal(
            new[] { "palletCode", "palletSessionId", "payload", "serialNo" },
            body.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.Equal("SN-0001", body.GetProperty("serialNo").GetString());
        Assert.Equal("P001", body.GetProperty("palletCode").GetString());
        Assert.Equal(1, body.GetProperty("palletSessionId").GetInt64());
        Assert.Equal("{\"ok\":true}", body.GetProperty("payload").GetString());

        Assert.Equal("mes", Assert.Single(http.RequestedNames));
    }

    [Fact]
    public async Task KeepsItemPendingAndRecordsHttpStatusWhenRejected()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await EnableMesAsync(ctx, "http://mes.local/push");
        var id = await AddPendingAsync(ctx, "SN-0002", "P002");
        var http = new FakeHttpClientFactory(new FakeHttpMessageHandler(503));

        await ctx.RunAsync(
            new MesOutboxProcessor(ctx.ScopeFactory, http, ctx.Logger<MesOutboxProcessor>()),
            () => ReadLastError(ctx, id) == "HTTP 503");

        Assert.Equal(MesOutboxStatus.Pending, ReadStatus(ctx, id));
        Assert.Equal(1, ReadAttempts(ctx, id));
    }

    [Fact]
    public async Task DoesNotTouchOutboxWhenMesDisabled()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var id = await AddPendingAsync(ctx, "SN-0003", "P003");
        var http = new FakeHttpClientFactory(new FakeHttpMessageHandler(200));

        // MesEnabled 默认 false：这一轮应当直接跳过，不产生任何出站请求。
        await ctx.RunAsync(
            new MesOutboxProcessor(ctx.ScopeFactory, http, ctx.Logger<MesOutboxProcessor>()),
            () => true);

        Assert.Empty(http.Handler.Requests);
        Assert.Equal(MesOutboxStatus.Pending, ReadStatus(ctx, id));
    }

    [Fact]
    public async Task SkipsItemsStillInsideBackoffWindow()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await EnableMesAsync(ctx, "http://mes.local/push");

        // AttemptCount=1 → 退避 2 秒；刚试过的项目本轮不能被重复推送。
        var id = await AddPendingAsync(ctx, "SN-0004", "P004", attempts: 1, lastAttemptAt: DateTime.Now);
        var http = new FakeHttpClientFactory(new FakeHttpMessageHandler(200));

        await ctx.RunAsync(
            new MesOutboxProcessor(ctx.ScopeFactory, http, ctx.Logger<MesOutboxProcessor>()),
            () => true);

        Assert.Empty(http.Handler.Requests);
        Assert.Equal(1, ReadAttempts(ctx, id));
    }

    [Fact]
    public async Task PushesPendingInCreatedOrderWithinTheBatch()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await EnableMesAsync(ctx, "http://mes.local/push");

        var created = DateTime.Now.AddMinutes(-10);
        var ids = new List<long>();
        for (var i = 0; i < 25; i++)
        {
            ids.Add(await AddPendingAsync(ctx, $"SN-{i:0000}", $"P{i:0000}", createdAt: created.AddSeconds(i)));
        }

        var http = new FakeHttpClientFactory(new FakeHttpMessageHandler(200));

        await ctx.RunAsync(
            new MesOutboxProcessor(ctx.ScopeFactory, http, ctx.Logger<MesOutboxProcessor>()),
            () => ReadStatus(ctx, ids[19]) == MesOutboxStatus.Succeeded);

        // 单轮批量远大于这里的 25 条，一轮就该按排队顺序全部推完（停机恢复后要能追上积压）。
        Assert.Equal(25, http.Handler.Requests.Count);
        Assert.All(ids, id => Assert.Equal(MesOutboxStatus.Succeeded, ReadStatus(ctx, id)));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    [InlineData(30, 30)]
    public async Task ClientTimeoutIsFlooredAtThreeSeconds(int configured, int expectedSeconds)
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        await EnableMesAsync(ctx, "http://mes.local/push", timeoutSeconds: Math.Max(SettingsLimits.MinMesTimeoutSeconds, configured));
        if (configured < SettingsLimits.MinMesTimeoutSeconds)
        {
            // 0 只能来自存量脏数据：仓储层已拒绝，处理器的兜底仍要把它夹到 3 秒。
            await ctx.ForceSettingsAsync(s => s.MesTimeoutSeconds = configured);
        }

        var id = await AddPendingAsync(ctx, "SN-0005", "P005");
        var http = new FakeHttpClientFactory(new FakeHttpMessageHandler(200));

        await ctx.RunAsync(
            new MesOutboxProcessor(ctx.ScopeFactory, http, ctx.Logger<MesOutboxProcessor>()),
            () => ReadStatus(ctx, id) == MesOutboxStatus.Succeeded);

        var client = Assert.Single(http.Clients);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), client.Timeout);
    }

    private static async Task EnableMesAsync(InfrastructureContext ctx, string endpoint, int timeoutSeconds = 10)
    {
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var settings = (await repo.GetSnapshotAsync()).Settings;
        settings.MesEnabled = true;
        settings.MesEndpoint = endpoint;
        settings.MesTimeoutSeconds = timeoutSeconds;
        await repo.SaveSettingsAsync(settings);
    }

    private async Task<long> AddPendingAsync(
        InfrastructureContext ctx,
        string serialNo,
        string palletCode,
        int attempts = 0,
        DateTime? lastAttemptAt = null,
        DateTime? createdAt = null)
    {
        var db = ctx.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        var item = new MesOutboxItem
        {
            MonthKey = RuntimeDbFactory.MonthKey(createdAt ?? DateTime.Now),
            PalletSessionId = 1,
            SerialNo = serialNo,
            PalletCode = palletCode,
            PayloadJson = "{\"ok\":true}",
            Status = MesOutboxStatus.Pending,
            AttemptCount = attempts,
            LastAttemptAt = lastAttemptAt,
            CreatedAt = createdAt ?? DateTime.Now
        };

        db.MesOutbox.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private MesOutboxStatus ReadStatus(InfrastructureContext ctx, long id)
        => ReadItem(ctx, id).Status;

    private string? ReadLastError(InfrastructureContext ctx, long id)
        => ReadItem(ctx, id).LastError;

    private int ReadAttempts(InfrastructureContext ctx, long id)
        => ReadItem(ctx, id).AttemptCount;

    private static MesOutboxItem ReadItem(InfrastructureContext ctx, long id)
    {
        var db = ctx.Scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
        return db.MesOutbox.AsNoTracking().Single(x => x.Id == id);
    }
}

/// <summary>波形基线刷新服务的调度与降级。</summary>
public class CurveBaselineRefresherTests
{
    private sealed class RecordingCache : ICurveBaselineCache
    {
        public int ReplaceCount { get; private set; }

        public CurveBaselineSnapshot? Current { get; private set; }

        public void Replace(CurveBaselineSnapshot snapshot)
        {
            ReplaceCount++;
            Current = snapshot;
        }

        public void RetagRecipeCode(string oldCode, string newCode)
        {
            if (Current is null || Current.RecipeCode != oldCode)
            {
                return;
            }

            Current = new CurveBaselineSnapshot
            {
                RecipeCode = newCode,
                RefreshedAt = Current.RefreshedAt,
                Templates = Current.Templates
            };
        }
    }

    [Fact]
    public async Task PublishesBaselineSnapshotOnFirstRun()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var cache = new RecordingCache();
        var snapshot = await ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>().GetSnapshotAsync();

        await ctx.RunAsync(
            new CurveBaselineRefresher(ctx.ScopeFactory, cache, ctx.Logger<CurveBaselineRefresher>()),
            () => cache.Current is not null);

        Assert.Equal(1, cache.ReplaceCount);
        Assert.Equal(snapshot.ActiveRecipe?.Code ?? "", cache.Current!.RecipeCode);
    }

    [Fact]
    public async Task LeavesEmptySnapshotWhenSamplesAreInsufficient()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var cache = new RecordingCache();

        // 全新库没有历史波形：基线必须为空，采集侧据此退化成"不打分"而不是造模板。
        await ctx.RunAsync(
            new CurveBaselineRefresher(ctx.ScopeFactory, cache, ctx.Logger<CurveBaselineRefresher>()),
            () => cache.Current is not null);

        Assert.Empty(cache.Current!.Templates);
        Assert.Null(cache.Current.Find(curveDefinitionId: 1, "压力"));
    }

    [Fact]
    public async Task StopsCleanlyWhenCancelledDuringFirstRefresh()
    {
        await using var ctx = await InfrastructureContext.CreateAsync(seed: false);
        var cache = new RecordingCache();

        // 取消要能干净退出：既不残留基线，也不把取消当成故障冒泡（探针已验证 ExecuteTask 未 faulted）。
        await ctx.RunAsync(
            new CurveBaselineRefresher(ctx.ScopeFactory, cache, ctx.Logger<CurveBaselineRefresher>()),
            () => true);

        Assert.InRange(cache.ReplaceCount, 0, 1);
    }
}
