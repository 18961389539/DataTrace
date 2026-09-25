using DataTrace.Application.Configuration;
using DataTrace.Application.Evaluation;
using DataTrace.Application.Realtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Simulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Tests;

/// <summary>
/// 采集扫描循环：连接重建的判定范围，以及配置类问题不再被静默吞掉。
/// </summary>
public class CollectionHostedServiceTests
{
    /// <summary>按需产生模拟驱动并记录创建次数，用来观察"哪些配置改动触发了连接重建"。</summary>
    private sealed class CountingDriverFactory : IPlcDriverFactory
    {
        public int CreateCount;

        public List<string> CreatedFor { get; } = [];

        public IPlcDriver Create(PlcConnection connection)
        {
            Interlocked.Increment(ref CreateCount);
            lock (CreatedFor)
            {
                CreatedFor.Add(connection.Name);
            }

            return new InMemoryPlcDriver();
        }
    }

    private static CollectionHostedService CreateService(InfrastructureContext ctx, IPlcDriverFactory factory)
        => new(
            ctx.ScopeFactory,
            factory,
            new StationCollectPipeline(
                ctx.ScopeFactory,
                ctx.Provider.GetRequiredService<IRuntimeStatusHub>(),
                ctx.Provider.GetRequiredService<ICollectEventBus>(),
                ctx.Provider.GetRequiredService<ICurveBaselineCache>(),
                ctx.Logger<StationCollectPipeline>()),
            ctx.Provider.GetRequiredService<IRuntimeStatusHub>(),
            ctx.Logger<CollectionHostedService>());

    /// <summary>启动后台服务，等条件成立再停机（比 HostedServiceProbe 更细，可分阶段断言）。</summary>
    private static async Task RunUntilAsync(
        CollectionHostedService service,
        Func<bool> condition,
        Func<string>? diagnostics = null,
        int timeoutMs = 15000)
    {
        using var cts = new CancellationTokenSource();
        await ((IHostedService)service).StartAsync(cts.Token);
        try
        {
            var startedAt = DateTime.UtcNow;
            while (!condition())
            {
                if ((DateTime.UtcNow - startedAt).TotalMilliseconds > timeoutMs)
                {
                    var detail = diagnostics?.Invoke() ?? "";
                    throw new TimeoutException(
                        "采集服务在超时前未满足条件" + (string.IsNullOrEmpty(detail) ? "" : $"；日志：{detail}"));
                }

                await Task.Delay(20);
            }
        }
        finally
        {
            cts.Cancel();
            await ((IHostedService)service).StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 15000)
    {
        var startedAt = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - startedAt).TotalMilliseconds > timeoutMs)
            {
                throw new TimeoutException("等待条件成立超时");
            }

            await Task.Delay(20);
        }
    }

    private static bool Logged(InfrastructureContext ctx, string fragment)
        => ctx.Logs.Snapshot().Any(x => x.Message.Contains(fragment));

    // ---------- 连接重建的判定范围（P9） ----------

    [Fact]
    public async Task ChangingOnlyStationConfig_doesNotRebuildPlcConnections()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var factory = new CountingDriverFactory();
        var service = CreateService(ctx, factory);
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var status = ctx.Provider.GetRequiredService<IRuntimeStatusHub>();

        // 连接建好之后再改工站配置：版本号会 +1，但 PLC 连接本身没变。
        var mutator = Task.Run(async () =>
        {
            await WaitUntilAsync(() => Volatile.Read(ref factory.CreateCount) >= 1);
            var station = (await repo.GetStationsAsync()).First();
            station.Name = $"{station.Name}-改名";
            await repo.SaveStationAsync(station);
        });

        // 服务认出这次变更后会刷新工站状态，用状态里的新名字作为"这一轮已经处理完"的信号。
        await RunUntilAsync(service, () => status.Stations.Any(x => x.StationName.EndsWith("-改名", StringComparison.Ordinal)));
        await mutator;

        // 只改点位/工站不该把 PLC 连接拆掉重建（现场表现就是产线全体断线重连）。
        Assert.Equal(1, Volatile.Read(ref factory.CreateCount));
    }

    [Fact]
    public async Task ChangingPlcConnection_rebuildsThatConnection()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var factory = new CountingDriverFactory();
        var service = CreateService(ctx, factory);
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var plcId = (await repo.GetPlcConnectionsAsync()).Single().Id;

        var mutator = Task.Run(async () =>
        {
            await WaitUntilAsync(() => Volatile.Read(ref factory.CreateCount) >= 1);
            var plc = await repo.GetPlcConnectionAsync(plcId);
            plc!.TimeoutMs = 4321;
            await repo.SavePlcConnectionAsync(plc);
        });

        // 连接参数变了必须重建，否则新超时永远不生效。
        await RunUntilAsync(service, () => Volatile.Read(ref factory.CreateCount) >= 2);
        await mutator;

        Assert.Equal(2, Volatile.Read(ref factory.CreateCount));
    }

    // ---------- 配置类问题不再静默（P1） ----------

    [Fact]
    public void Signature_coversDriverAndPlanningRelevantFieldsOnly()
    {
        var plc = new PlcConnection
        {
            Id = 7,
            Name = "一号线",
            Brand = PlcBrand.SiemensS7,
            Host = "10.0.0.5",
            Port = 102,
            TimeoutMs = 2000,
            MergeGapWords = 16,
            Extra = "0,1",
            Heartbeat = new HeartbeatSettings { Address = "MW10", IntervalMs = 1000, Enabled = true }
        };

        var baseline = PlcConnectionSignature.Of(plc);
        Assert.Equal(baseline, PlcConnectionSignature.Of(Clone(plc)));

        // 影响驱动/读计划的每一项都必须让签名变化，否则改了参数却不会重建连接。
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Name = "二号线")));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Host = "10.0.0.6")));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Port = 103)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.TimeoutMs = 5000)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.MergeGapWords = 32)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.StringHighByteFirst = false)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.FloatWordOrder = DataTrace.Domain.Enums.FloatWordOrder.ABCD)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Extra = "0,2")));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Enabled = false)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Heartbeat!.Address = "MW12")));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Heartbeat!.IntervalMs = 2000)));
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Heartbeat = null)));

        // Brand 变了也要重建（换了品牌就是另一套协议与解析器）。
        Assert.NotEqual(baseline, PlcConnectionSignature.Of(Clone(plc, x => x.Brand = PlcBrand.ModbusTcp)));
    }

    private static PlcConnection Clone(PlcConnection source, Action<PlcConnection>? mutate = null)
    {
        var copy = new PlcConnection
        {
            Id = source.Id,
            Name = source.Name,
            Brand = source.Brand,
            Host = source.Host,
            Port = source.Port,
            TimeoutMs = source.TimeoutMs,
            FloatWordOrder = source.FloatWordOrder,
            StringHighByteFirst = source.StringHighByteFirst,
            MergeGapWords = source.MergeGapWords,
            Enabled = source.Enabled,
            Extra = source.Extra,
            Heartbeat = source.Heartbeat is null
                ? null
                : new HeartbeatSettings
                {
                    Address = source.Heartbeat.Address,
                    IntervalMs = source.Heartbeat.IntervalMs,
                    Mode = source.Heartbeat.Mode,
                    Enabled = source.Heartbeat.Enabled
                }
        };
        mutate?.Invoke(copy);
        return copy;
    }

    [Fact]
    public async Task BitAddressTrigger_isReportedInsteadOfSilentlySkipped()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var station = (await repo.GetStationsAsync()).First();

        // 位地址解析得出来，但进不了读计划：以前这里静默 continue，工站永远不触发且无人知道。
        station.TriggerAddress = "M100";
        await repo.SaveStationAsync(station);

        var service = CreateService(ctx, new CountingDriverFactory());
        await RunUntilAsync(service, () => Logged(ctx, "是位地址"), () => ctx.Logs.Dump());

        var warning = ctx.Logs.Snapshot().First(x => x.Message.Contains("是位地址")).Message;
        Assert.Contains("M100", warning);
        Assert.Contains(station.Code, warning);
        Assert.Contains("字地址", warning);
    }

    [Fact]
    public async Task UnparsableHeartbeatAddress_isReported()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var plc = (await repo.GetPlcConnectionsAsync()).Single();
        plc.Heartbeat!.Address = "ZZ999";
        await repo.SavePlcConnectionAsync(plc);

        var service = CreateService(ctx, new CountingDriverFactory());
        await RunUntilAsync(service, () => Logged(ctx, "心跳地址"));

        Assert.Contains(ctx.Logs.Snapshot(), x => x.Message.Contains("ZZ999"));
    }

    [Fact]
    public async Task FailingHeartbeat_retriesAtHeartbeatIntervalNotEveryScan()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();

        // 心跳周期 1 秒，扫描周期 200 毫秒：写失败时若只在成功时推进时间戳，
        // 每个扫描周期都会重试一次，把扫描循环拖慢 5 倍。
        var plc = (await repo.GetPlcConnectionsAsync()).Single();
        plc.Heartbeat!.IntervalMs = 1000;
        await repo.SavePlcConnectionAsync(plc);

        var factory = new HeartbeatFailingDriverFactory();
        var service = CreateService(ctx, factory);

        await RunUntilAsync(service, () => Volatile.Read(ref factory.HeartbeatWrites) >= 1);
        var afterFirstFailure = Volatile.Read(ref factory.HeartbeatWrites);
        await Task.Delay(900);

        // 900ms 内（不足一个心跳周期）不该再出现成批的重试。
        Assert.Equal(afterFirstFailure, Volatile.Read(ref factory.HeartbeatWrites));
    }

    /// <summary>
    /// 历史脏配置的兜底：触发值等于回写码时，写完响应码寄存器仍等于触发值。
    /// </summary>
    /// <remarks>
    /// 这种情况以前会一个扫描周期采一次同一托盘（实测约 2 秒落库 3 条以上）。
    /// 现在采集侧要跳过该工站并留下明确说明，而不是继续循环。
    /// </remarks>
    [Fact]
    public async Task WriteBackTriggerValue_isSkippedInsteadOfLooping()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);

        // 绕开仓储（仓储会拒绝这种配置）直接改库，模拟现场已经存在的坏数据。
        using (var scope = harness.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var stored = await db.Stations.FirstAsync(x => x.Id == station.Id);
            stored.TriggerValue = ResultCodes.Success;
            await db.SaveChangesAsync();
        }

        SimulatorScenario.LoadStationCycle(
            harness.Simulator, harness.Plc, station, "P0009", new SimulatedCycleOptions { Random = new Random(7) });
        // 触发位正好等于触发值：等同于"上一轮写完响应码后没被清掉"，也就是无限重复采集的现场形态。
        harness.Simulator.SetWord(station.TriggerAddress, (ushort)ResultCodes.Success);
        Assert.Equal(ResultCodes.Success, (short)harness.Simulator.GetWord(station.TriggerAddress));

        await using var ctx = new HarnessServiceContext(harness);
        await RunUntilAsync(ctx.Service, () => ctx.Logged("回写码"));

        // 跳过而不是循环：这条托盘一条记录都不该产生。
        Assert.Empty((await harness.QueryAsync("P0009")).Items);
        var warning = harness.Logs.Snapshot().First(x => x.Message.Contains("回写码")).Message;
        Assert.Contains(station.Code, warning);
    }

    /// <summary>
    /// 一条线没有启用的末站：会话永远不会关闭，必须给出说明。
    /// </summary>
    [Fact]
    public async Task LineWithoutEnabledLastStation_isReported()
    {
        await using var ctx = await InfrastructureContext.CreateAsync();
        var repo = ctx.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
        var last = (await repo.GetStationsAsync()).Single(x => x.IsLastStation);
        last.Enabled = false;
        await repo.SaveStationAsync(last);

        var service = CreateService(ctx, new CountingDriverFactory());
        await RunUntilAsync(service, () => Logged(ctx, "没有任何启用的末站"));

        Assert.Contains(ctx.Logs.Snapshot(), x => x.Message.Contains("会话永远不会关闭"));
    }

    /// <summary>把采集环境包装成与 InfrastructureContext 一致的两件事：服务实例 + 日志查询。</summary>
    private sealed class HarnessServiceContext : IAsyncDisposable
    {
        private readonly CollectHarness _harness;

        public HarnessServiceContext(CollectHarness harness)
        {
            _harness = harness;
            Service = new CollectionHostedService(
                harness.Provider.GetRequiredService<IServiceScopeFactory>(),
                harness.Provider.GetRequiredService<IPlcDriverFactory>(),
                harness.Pipeline,
                harness.StatusHub,
                harness.Provider.GetRequiredService<ILogger<CollectionHostedService>>());
        }

        public CollectionHostedService Service { get; }

        public bool Logged(string fragment) => _harness.Logs.Snapshot().Any(x => x.Message.Contains(fragment));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>心跳写入一律失败的驱动，用于观察重试节奏。</summary>
    private sealed class HeartbeatFailingDriverFactory : IPlcDriverFactory
    {
        public int HeartbeatWrites;

        public IPlcDriver Create(PlcConnection connection) => new FailingWriteDriver(() => Interlocked.Increment(ref HeartbeatWrites));

        private sealed class FailingWriteDriver(Action onWrite) : IPlcDriver
        {
            private readonly MitsubishiAddressParser _parser = new();

            public PlcBrand Brand => PlcBrand.Simulator;

            public PlcCapabilities Capabilities => PlcCapabilities.Simulator;

            public bool IsConnected { get; private set; }

            public bool TryParseAddress(string text, out PlcAddress address) => _parser.TryParse(text, out address);

            public Task ConnectAsync(CancellationToken cancellationToken = default)
            {
                IsConnected = true;
                return Task.CompletedTask;
            }

            public Task DisconnectAsync(CancellationToken cancellationToken = default)
            {
                IsConnected = false;
                return Task.CompletedTask;
            }

            public Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
                => Task.FromResult(new ushort[wordCount]);

            public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
            {
                onWrite();
                // 驱动保持"已连接"（心跳写失败属于业务错误），避免连带触发连接熔断。
                throw new PlcDriverException("模拟心跳写入失败");
            }

            public ValueTask DisposeAsync()
            {
                IsConnected = false;
                return ValueTask.CompletedTask;
            }
        }
    }
}



