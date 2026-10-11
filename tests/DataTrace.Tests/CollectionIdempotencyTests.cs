using System.Text.Json;
using System.Diagnostics;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Storage;
using DataTrace.Plc;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Queue;
using DataTrace.Plc.Simulator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataTrace.Tests;

/// <summary>
/// 采集幂等与在制索引对账：故障注入验证（对应质量审查 R01 / R02 的验收口径）。
/// R01：回写失败的已提交件，设备无稳定事件身份时持久阻塞，绝不自动回写旧码或重新采集。
/// R02：月库提交后、索引更新前的硬退出窗口留下的未决意图，重启后由对账器条件重放。
/// </summary>
public class CollectionIdempotencyTests
{
    private sealed class AlwaysFailingCollectWriter : ICollectWriter
    {
        public int SaveAttempts { get; private set; }
        public CollectSaveRequest? LastRequest { get; private set; }
        public Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            LastRequest = request;
            throw new IOException("runtime database unavailable");
        }
        public Task RepairPostCommitStateAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
            => throw new IOException("runtime database unavailable");
        public Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default)
            => throw new IOException("runtime database unavailable");
    }

    private sealed class QuotaFullSpool : ISpoolStore
    {
        public int SaveAttempts { get; private set; }
        public ISpoolStore? RecoveryStore { get; set; }
        public Task<string> SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            return RecoveryStore is null
                ? Task.FromException<string>(new IOException("spool quota exceeded"))
                : RecoveryStore.SaveAsync(request, cancellationToken);
        }
        public Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListAsync(CancellationToken cancellationToken = default)
            => RecoveryStore?.ListAsync(cancellationToken)
                ?? Task.FromResult<IReadOnlyList<(string, CollectSaveRequest)>>([]);
        public Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListPageAsync(int skip, int take, CancellationToken cancellationToken = default)
            => RecoveryStore?.ListPageAsync(skip, take, cancellationToken)
                ?? Task.FromResult<IReadOnlyList<(string, CollectSaveRequest)>>([]);
        public Task<bool> HasMonthEntriesAsync(string monthKey, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasPendingForStationAsync(int stationId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CollectSaveRequest?> ReadOneAsync(string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult<CollectSaveRequest?>(null);
        public Task<SpoolBacklog> DescribeAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SpoolBacklog(0, null));
        public Task<IReadOnlyList<SpoolEntry>> ListEntriesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpoolEntry>>([]);
        public Task NoteFailureAsync(string fileName, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AppliedWriteWithLostAckDriver(InMemoryPlcDriver inner, string triggerAddress) : IPlcDriver
    {
        public int ResultWriteCount { get; private set; }
        public DataTrace.Domain.Enums.PlcBrand Brand => inner.Brand;
        public DataTrace.Plc.Abstractions.PlcCapabilities Capabilities => inner.Capabilities;
        public bool IsConnected => inner.IsConnected;
        public bool TryParseAddress(string text, out PlcAddress address) => inner.TryParseAddress(text, out address);
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
            => inner.ReadWordsAsync(start, wordCount, cancellationToken);

        public async Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
        {
            ResultWriteCount++;
            await inner.WriteWordsAsync(start, words, cancellationToken);
            // Model the dangerous field case: the old acknowledgement reached the PLC, then a new
            // event asserted the trigger before the client received its write acknowledgement.
            inner.SetWord(triggerAddress, (ushort)ResultCodes.Trigger);
            throw new PlcDriverException("write applied, acknowledgement lost; next trigger is a new event");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void LoadCycle(CollectHarness harness, Domain.Entities.Station station, string palletCode)
        => SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, palletCode,
            new SimulatedCycleOptions { Random = new Random(7) });

    [Fact]
    public async Task Pending_write_back_without_event_identity_stays_blocked_instead_of_recollection()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0101");

        // 第一件正常采集：落库、回写成功、触发位清成响应码。
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        var item = Assert.Single((await harness.QueryAsync("P0101")).Items);

        // 构造回写失败的现场形态：触发位回到触发值 + 一条"已提交、待确认"留证。
        // 没有设备事件号时，触发值不能区分原件和新件，必须进入人工阻塞。
        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        await pendingStore.SaveAsync(new PendingWriteBack
        {
            CollectRecordId = item.Record.Id,
            PalletCode = "P0101",
            SerialNo = item.Record.SerialNo,
            StationId = station.Id,
            StationCode = station.Code,
            MonthKey = item.MonthKey,
            PalletSessionId = item.Record.PalletSessionId,
            ResultCode = ResultCodes.Success,
            CreatedAt = DateTime.Now,
            NextAttemptAt = DateTime.Now.AddSeconds(-1)
        });
        harness.Simulator.SetWord(station.TriggerAddress, (ushort)ResultCodes.Trigger);

        await using var ctx = new CollectionHostedServiceTests.HarnessServiceContext(harness);
        await CollectionHostedServiceTests.RunUntilAsync(
            ctx.Service, () => ctx.Logged("持久阻塞"));

        // 同一件只采一遍：没有新记录、没有新流水号。
        var after = Assert.Single((await harness.QueryAsync("P0101")).Items);
        Assert.Equal(item.Record.Id, after.Record.Id);

        // 未经人工核对不得销账，旧响应码不能写到可能已经换件的新触发。
        var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        Assert.True(pending.ManualReviewRequired);
        await CollectionHostedServiceTests.WaitUntilAsync(() =>
            harness.StatusHub.Stations.Single(x => x.StationId == station.Id).LastRecordId == pending.CollectRecordId);
        Assert.Equal(pending.CollectRecordId, harness.StatusHub.Stations.Single(x => x.StationId == station.Id).LastRecordId);
        Assert.Equal(ResultCodes.Trigger, (short)harness.Simulator.GetWord(station.TriggerAddress));
    }

    [Fact]
    public async Task Lost_write_ack_is_never_retried_over_a_new_trigger_event()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0109");
        var driver = new AppliedWriteWithLostAckDriver(harness.Simulator, station.TriggerAddress);
        await using var queue = new PlcRequestQueue(driver, ownsDriver: false);

        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, queue, CancellationToken.None);

        Assert.Equal(1, driver.ResultWriteCount);
        Assert.Equal(ResultCodes.Trigger, (short)harness.Simulator.GetWord(station.TriggerAddress));
        var pending = await harness.Provider.GetRequiredService<IPendingWriteBackStore>()
            .FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        Assert.True(pending.ManualReviewRequired);
        Assert.Contains("单次", harness.StatusHub.Stations.Single(x => x.StationId == station.Id).LastError);
    }

    [Fact]
    public async Task Database_and_spool_failure_preserve_trigger_and_hold_the_original_request()
    {
        var writer = new AlwaysFailingCollectWriter();
        var spool = new QuotaFullSpool();
        await using var harness = await CollectHarness.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<ICollectWriter>();
            services.AddSingleton<ICollectWriter>(writer);
            services.RemoveAll<ISpoolStore>();
            services.AddSingleton<ISpoolStore>(spool);
        });
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0110");

        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, harness.Queue, CancellationToken.None);

        Assert.Equal(1, writer.SaveAttempts);
        Assert.Equal(1, spool.SaveAttempts);
        Assert.True(harness.Pipeline.HasHeldPersistence(station.Id));
        Assert.Equal(ResultCodes.Trigger, (short)harness.Simulator.GetWord(station.TriggerAddress));
        var status = Assert.Single(harness.StatusHub.Stations, item => item.StationId == station.Id);
        Assert.Equal(StationRuntimeState.Fault, status.State);
        Assert.Contains("月库与本地 spool", status.LastError ?? "");
        Assert.Contains("P0110", status.LastPalletCode ?? "");

        // A direct duplicate call is also stopped before reading/reallocating; retry is storage-only.
        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, harness.Queue, CancellationToken.None);
        Assert.Equal(1, writer.SaveAttempts);
        Assert.Equal(1, spool.SaveAttempts);
        Assert.Equal(ResultCodes.Trigger, (short)harness.Simulator.GetWord(station.TriggerAddress));

        spool.RecoveryStore = new FileSpoolStore(Path.Combine(harness.Root, "spool"));
        await Task.Delay(TimeSpan.FromSeconds(2.1)); // respect the first bounded retry backoff
        await harness.Pipeline.RetryHeldPersistenceAsync(station.Id, ResultCodes.Trigger, CancellationToken.None);
        Assert.False(harness.Pipeline.HasHeldPersistence(station.Id));
        Assert.Equal(2, writer.SaveAttempts);
        Assert.Equal(2, spool.SaveAttempts);
        var persisted = Assert.Single(await spool.RecoveryStore.ListAsync());
        Assert.Equal(writer.LastRequest!.Record.SerialNo, persisted.Request.Record.SerialNo);
        Assert.Equal("P0110", persisted.Request.Record.PalletCode);
        Assert.True(persisted.Request.RequiresManualReviewOnPersistence);
        Assert.Equal(ResultCodes.Trigger, (short)harness.Simulator.GetWord(station.TriggerAddress));
    }

    [Fact]
    public async Task Later_station_confirmation_deletes_pending_from_session_month_only()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var firstStation = harness.Station(0);
        var laterStation = harness.Station(1);
        var oldTriggerTime = new DateTime(2026, 9, 30, 23, 59, 0);
        var coordinator = new CollectSessionCoordinator(
            harness.Provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CollectSessionCoordinator>.Instance);
        var first = await coordinator.PersistAsync(
            firstStation,
            harness.Snapshot,
            "P0111",
            oldTriggerTime,
            Stopwatch.StartNew(),
            source: null,
            new CollectAssessment { TagValues = [], Products = [], Curves = [] },
            CancellationToken.None);
        Assert.Equal("202609", first.MonthKey);
        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        await pendingStore.DeleteByRecordIdAsync(first.MonthKey, first.Record.Id);

        var triggerMonth = RuntimeDbFactory.MonthKey(DateTime.Now);
        Assert.NotEqual(first.MonthKey, triggerMonth);
        // The next row in the old session month will get Id=2. A guard with the same numeric record
        // identity in the trigger month must survive that later-station acknowledgement.
        await pendingStore.SaveAsync(new PendingWriteBack
        {
            CollectRecordId = first.Record.Id + 1,
            PalletCode = "P-OTHER-MONTH",
            SerialNo = "SN-OTHER-MONTH",
            StationId = 999,
            StationCode = "ST999",
            MonthKey = triggerMonth,
            ResultCode = ResultCodes.QualityRejected,
            CreatedAt = DateTime.Now,
            NextAttemptAt = DateTime.Now
        });

        LoadCycle(harness, laterStation, "P0111");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(laterStation));

        await using (var oldDb = harness.Provider.GetRequiredService<RuntimeDbFactory>().Open(first.MonthKey))
        {
            Assert.False(await oldDb.PendingWriteBacks.AnyAsync(x => x.CollectRecordId == first.Record.Id + 1));
        }
        await using (var currentDb = harness.Provider.GetRequiredService<RuntimeDbFactory>().Open(triggerMonth))
        {
            var survivor = await currentDb.PendingWriteBacks.SingleAsync(x => x.CollectRecordId == first.Record.Id + 1);
            Assert.Equal("SN-OTHER-MONTH", survivor.SerialNo);
            Assert.Equal(999, survivor.StationId);
        }
    }

    [Fact]
    public async Task Session_index_intent_replays_after_hard_exit()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0102");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        var item = Assert.Single((await harness.QueryAsync("P0102")).Items);

        // 模拟硬退出窗口：月库已提交、config.db 索引更新没来得及发生（进程被杀），
        // 只留下一条同事务写好的未决意图。
        var sessions = harness.Scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        await sessions.RemoveByPalletAsync("P0102");

        var payload = JsonSerializer.Serialize(new ActiveSessionIndex
        {
            PalletCode = "P0102",
            SerialNo = item.Record.SerialNo,
            MonthKey = item.MonthKey,
            SessionId = item.Record.PalletSessionId,
            StartTime = item.Record.TriggerTime
        });
        var factory = harness.Provider.GetRequiredService<RuntimeDbFactory>();
        await using (var db = factory.Open(item.MonthKey))
        {
            db.SessionIndexIntents.Add(new SessionIndexIntent
            {
                PalletCode = "P0102",
                SerialNo = item.Record.SerialNo,
                MonthKey = item.MonthKey,
                Operation = 0,
                PayloadJson = payload,
                CreatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
        }

        var relay = harness.Provider.GetRequiredService<ISessionIndexRelay>();
        var applied = await relay.RelayPendingAsync(100);

        // 索引被补回，且不会重复新建 ORPHAN 会话。
        Assert.Equal(1, applied);
        var restored = await sessions.FindByPalletAsync("P0102");
        Assert.NotNull(restored);
        Assert.Equal(item.Record.SerialNo, restored.SerialNo);
    }

    [Fact]
    public async Task Session_index_intent_stale_intent_is_voided_not_applied()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0103");

        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));

        // 该托盘已被一条"新流水"接管（现场形态：旧事件的索引更新晚到了）。
        var sessions = harness.Scope.ServiceProvider.GetRequiredService<IActiveSessionStore>();
        await sessions.UpsertAsync(new ActiveSessionIndex
        {
            PalletCode = "P0103",
            SerialNo = "NEW-SERIAL",
            MonthKey = "210001",
            StartTime = DateTime.Now
        });

        var factory = harness.Provider.GetRequiredService<RuntimeDbFactory>();
        var monthKey = Assert.Single((await harness.QueryAsync("P0103")).Items).MonthKey;
        await using (var db = factory.Open(monthKey))
        {
            db.SessionIndexIntents.Add(new SessionIndexIntent
            {
                PalletCode = "P0103",
                SerialNo = "OLD-SERIAL",
                MonthKey = monthKey,
                Operation = 0,
                PayloadJson = JsonSerializer.Serialize(new ActiveSessionIndex
                {
                    PalletCode = "P0103",
                    SerialNo = "OLD-SERIAL",
                    MonthKey = monthKey,
                    StartTime = DateTime.Now.AddMinutes(-1)
                }),
                CreatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
        }

        var relay = harness.Provider.GetRequiredService<ISessionIndexRelay>();
        var applied = await relay.RelayPendingAsync(100);

        // 过期意图作废（不计入 applied），绝不能用旧流水覆盖新流水留下的索引。
        Assert.Equal(0, applied);
        var current = await sessions.FindByPalletAsync("P0103");
        Assert.NotNull(current);
        Assert.Equal("NEW-SERIAL", current.SerialNo);
    }

    // ---------- 复审 F01–F03/F06 的回归 ----------

    /// <summary>与 RuntimeStore 序列化意图使用的命名策略保持一致（Web/camelCase）。</summary>
    private static readonly JsonSerializerOptions IntentJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>配置库在对账链路上不可用的替身：模拟"提交后索引更新失败"的现场。</summary>
    private sealed class FailingActiveSessionStore : IActiveSessionStore
    {
        public Task<ActiveSessionIndex?> FindByPalletAsync(string palletCode, CancellationToken cancellationToken = default)
            => Task.FromResult<ActiveSessionIndex?>(null);

        public Task UpsertAsync(ActiveSessionIndex session, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("config.db 不可用（模拟提交后索引失败）");

        public Task RemoveByPalletAsync(string palletCode, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ActiveSessionIndex>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActiveSessionIndex>>([]);
    }

    /// <summary>索引永远可用的替身：聚焦"待确认登记"本身的测试。</summary>
    private sealed class NoopActiveSessionStore : IActiveSessionStore
    {
        public Task<ActiveSessionIndex?> FindByPalletAsync(string palletCode, CancellationToken cancellationToken = default)
            => Task.FromResult<ActiveSessionIndex?>(null);

        public Task UpsertAsync(ActiveSessionIndex session, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveByPalletAsync(string palletCode, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ActiveSessionIndex>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ActiveSessionIndex>>([]);
    }

    private static CollectSaveRequest LiveRequest(string palletCode, string serialNo)
        => new()
        {
            MonthKey = RuntimeDbFactory.MonthKey(DateTime.Now),
            Record = new CollectRecord
            {
                PalletCode = palletCode,
                SerialNo = serialNo,
                StationId = 10,
                StationCode = "ST010",
                TriggerTime = DateTime.Now,
                CompleteTime = DateTime.Now,
                ResultCode = ResultCodes.Success,
                Judgement = Judgement.Ok
            },
            UpsertSession = new PalletSession
            {
                SerialNo = serialNo,
                PalletCode = palletCode,
                StartTime = DateTime.Now,
                Status = SessionStatus.Open
            }
        };

    /// <summary>
    /// F04 回归：实时件提交即登记"待确认"（落库事务内，不是回写失败后才补登记）；
    /// 补传重放（RequiresWriteBack=false）不登记 —— 历史件没有回写方，登记了就是永远未决的垃圾。
    /// </summary>
    [Fact]
    public async Task SaveAsync_registers_confirmation_intent_only_for_live_collection()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(Path.Combine(workspace.Root, "runtime"));
        var curves = new CurveFileStore(Path.Combine(workspace.Root, "curves"));
        var store = new RuntimeStore(factory, curves, new NoopActiveSessionStore());

        var live = LiveRequest("P0400", "SN-P0400");
        await store.SaveAsync(live);
        await using (var db = factory.Open(live.MonthKey))
        {
            var pending = await db.PendingWriteBacks.AsNoTracking().SingleAsync();
            Assert.Equal("SN-P0400", pending.SerialNo);
            Assert.Equal(ResultCodes.Success, pending.ResultCode);
            Assert.Null(pending.ConfirmedAt);
        }

        var replay = LiveRequest("P0401", "SN-P0401");
        replay.RequiresWriteBack = false;
        await store.SaveAsync(replay);
        await using (var db = factory.Open(live.MonthKey))
        {
            Assert.Equal("SN-P0400", (await db.PendingWriteBacks.AsNoTracking().SingleAsync()).SerialNo);
        }
    }

    /// <summary>
    /// F04 回归：回写失败注入下的真实闭环 —— 落库事务登记留证 → 回写失败不销账 →
    /// 会话被人工关闭后，确认重试必须<b>保留并持久阻塞</b>留证（旧结果码不能写给可能已换件的触发位）。
    /// </summary>
    [Fact]
    public async Task Confirmation_retry_preserves_closed_session_pending_as_manual_review_block()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        harness.Plc.Heartbeat = new HeartbeatSettings
        {
            PlcConnectionId = harness.Plc.Id,
            PlcConnection = harness.Plc,
            Address = "D500"
        };
        LoadCycle(harness, station, "P0402");

        // 回写失败注入：读取不受影响，判定/落库/登记照常，只有回写这步反复失败。
        // 回写失败时触发位不被清成响应码 —— RunAsync 返回的是寄存器里的触发值。
        harness.Simulator.SetFailWrites(true);
        Assert.Equal(ResultCodes.Trigger, await harness.RunAsync(station));

        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        Assert.Equal(ResultCodes.Success, pending.ResultCode);
        var committed = Assert.Single((await harness.QueryAsync("P0402")).Items);
        Assert.Equal(committed.Record.Id, pending.CollectRecordId);
        Assert.Equal(station.PlcConnectionId, pending.PlcConnectionId);
        Assert.Equal(station.TriggerAddress, pending.TriggerAddress);
        Assert.Equal(station.TriggerValue, pending.TriggerValue);
        Assert.False(string.IsNullOrWhiteSpace(pending.ConnectionFingerprint));

        // 人工关闭了这件的会话（现场：这托盘被拿走了）。
        var factory = harness.Provider.GetRequiredService<RuntimeDbFactory>();
        await using (var db = factory.Open(pending.MonthKey))
        {
            var session = await db.PalletSessions.SingleAsync(x => x.Id == pending.PalletSessionId);
            session.Status = SessionStatus.Closed;
            session.EndTime = DateTime.Now;
            await db.SaveChangesAsync();
        }

        harness.Simulator.SetFailWrites(false);
        var confirmed = await harness.Pipeline.RetryPendingConfirmationAsync(
            station, harness.Plc, harness.Snapshot, harness.Queue, pending, CancellationToken.None);

        Assert.False(confirmed);
        var blocked = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(blocked);
        Assert.True(blocked.ManualReviewRequired);
        Assert.Contains("事件身份", blocked.ManualReviewReason);
        Assert.Equal(ResultCodes.Trigger, unchecked((short)harness.Simulator.GetWord(station.TriggerAddress)));
    }

    /// <summary>
    /// F04 回归：即使留证刚刚产生，也必须停止自动回写 —— 设备没有事件号协议，
    /// 单靠托盘码/触发值无法证明事件身份，可能把旧码写给换件后的新事件。
    /// </summary>
    [Fact]
    public async Task Confirmation_retry_blocks_on_stale_pending()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0403");

        harness.Simulator.SetFailWrites(true);
        Assert.Equal(ResultCodes.Trigger, await harness.RunAsync(station));

        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);

        // 留证立即进入人工核对；登记时长不能成为自动写旧码的身份依据。
        var factory = harness.Provider.GetRequiredService<RuntimeDbFactory>();
        await using (var db = factory.Open(pending.MonthKey))
        {
            var row = await db.PendingWriteBacks.SingleAsync(x => x.Id == pending.Id);
            row.CreatedAt = DateTime.Now.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        harness.Simulator.SetFailWrites(false);
        // 重读留证，验证首次处理会将持久行转为人工阻塞。
        var stalePending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(stalePending);
        var confirmed = await harness.Pipeline.RetryPendingConfirmationAsync(
            station, harness.Plc, harness.Snapshot, harness.Queue, stalePending, CancellationToken.None);

        Assert.False(confirmed);
        var blocked = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(blocked);
        Assert.True(blocked.ManualReviewRequired);
    }

    [Fact]
    public async Task Manual_review_resolution_is_audited_and_keeps_the_event_row()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(Path.Combine(workspace.Root, "runtime"));
        var store = new PendingWriteBackStore(factory);
        var month = RuntimeDbFactory.MonthKey(DateTime.Now);
        await store.SaveAsync(new PendingWriteBack
        {
            MonthKey = month, SerialNo = "SN-MANUAL-1", PalletCode = "P-MANUAL-1", StationId = 91,
            StationCode = "ST091", ResultCode = ResultCodes.Success, CreatedAt = DateTime.Now,
            NextAttemptAt = DateTime.Now
        });
        var saved = await store.FindOldestUnconfirmedAsync(91);
        Assert.NotNull(saved);
        await store.RequireManualReviewAsync(month, saved.Id, "test block");
        Assert.Single(await store.ListManualReviewAsync());

        var resolvedAt = DateTime.Now;
        Assert.True(await store.ResolveManualReviewAsync(month, saved.Id, "engineer", "DeviceAlreadyAcknowledged: checked", "trigger=0", resolvedAt));
        Assert.Empty(await store.ListManualReviewAsync());
        await using var db = factory.Open(month);
        var retained = await db.PendingWriteBacks.AsNoTracking().SingleAsync();
        Assert.Equal(resolvedAt, retained.ConfirmedAt);
        Assert.Equal("engineer", retained.ManualReviewResolvedBy);
        Assert.Equal("DeviceAlreadyAcknowledged: checked", retained.ManualReviewResolution);
        Assert.Equal("trigger=0", retained.ManualReviewDeviceStateEvidence);
        await store.DeleteAsync(month, saved.Id);
        await using (var verifyDb = factory.Open(month))
            Assert.True(await verifyDb.PendingWriteBacks.AnyAsync(x => x.Id == saved.Id));
    }

    /// <summary>
    /// F05 回归：后续工站判定用<b>冻结的生效限值</b>—— 当前配置的点位默认上限改到天文数字，
    /// 冻结表里被收紧到极小的点位仍按冻结值判 NG；反向（放宽到不判）由同一张表保证。
    /// </summary>
    [Fact]
    public async Task Frozen_limits_survive_config_changes_for_the_same_piece()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var first = harness.Station(0);
        LoadCycle(harness, first, "P0404");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(first));

        var second = harness.Station(1);
        using (var scope = harness.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var tag = await db.Tags.AsNoTracking().FirstAsync(t => t.StationId == second.Id);
            var sessionRow = await db.ActiveSessions.SingleAsync(x => x.PalletCode == "P0404");

            // 冻结表里把该点位的规格上限收紧到极小：冻结判定必 NG。
            var node = System.Text.Json.Nodes.JsonNode.Parse(sessionRow.RecipeSnapshot!)!;
            foreach (var limit in node["limits"]!.AsArray())
            {
                if ((int)limit!["tagId"]! == tag.Id)
                {
                    // Keep the manually frozen limit internally consistent. The original
                    // lower bound was greater than this tiny upper bound, so the strict
                    // snapshot validator correctly treated that test fixture as corrupted.
                    limit["lower"] = null;
                    limit["upper"] = 1e-9;
                }
            }

            sessionRow.RecipeSnapshot = node.ToJsonString();
            // 当前配置的默认上限放到天文数字：若判定误用当前配置，这个点位不会超限。
            var mutable = await db.Tags.FirstAsync(t => t.StationId == second.Id && t.Id == tag.Id);
            mutable.UpperLimit = 1e9;
            await db.SaveChangesAsync();
        }

        LoadCycle(harness, second, "P0404");
        Assert.Equal(ResultCodes.QualityRejected, await harness.RunAsync(second));
    }

    [Fact]
    public async Task Frozen_curve_criteria_and_acquisition_definition_survive_config_changes()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var first = harness.Station(0);
        var second = harness.Station(1);
        second.Curves.Single().Criteria.Add(new CurveCriterion { PeakMin = 999 });
        LoadCycle(harness, second, "P0406");

        LoadCycle(harness, first, "P0406");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(first));

        // 当前定义被放宽；会话内应继续使用首站快照中的波形判据。
        second.Curves.Single().Criteria.Clear();
        Assert.Equal(ResultCodes.QualityRejected, await harness.RunAsync(second));
        var item = Assert.Single((await harness.QueryAsync("P0406")).Items, x => x.Record.StationId == second.Id);
        var record = await harness.RuntimeStore.GetRecordAsync(item.MonthKey, item.Record.Id);
        Assert.Contains("波形异常", Assert.Single(record!.Products).NgReason);
    }

    /// <summary>
    /// F05 回归：快照损坏必须阻塞判定（本站故障、不落库），绝不静默回落当前配置换规则。
    /// </summary>
    [Fact]
    public async Task Corrupted_recipe_snapshot_blocks_judgement_instead_of_falling_back()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var first = harness.Station(0);
        LoadCycle(harness, first, "P0405");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(first));

        var second = harness.Station(1);
        using (var scope = harness.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var sessionRow = await db.ActiveSessions.SingleAsync(x => x.PalletCode == "P0405");
            sessionRow.RecipeSnapshot = "{ 这不是合法 JSON";
            await db.SaveChangesAsync();
        }

        LoadCycle(harness, second, "P0405");
        // 快照结构损坏属于判定依据故障（InternalError），不能伪装成 PLC 通讯失败。
        Assert.Equal(ResultCodes.InternalError, await harness.RunAsync(second));

        // 阻塞的那一件不落库：这条托盘仍然只有首站一条记录。
        Assert.Single((await harness.QueryAsync("P0405")).Items);
    }

    /// <summary>读操作挂到被取消才返回的替身驱动：模拟停机边界上"正在执行"的 PLC 请求。</summary>
    private sealed class HangingPlcDriver : IPlcDriver
    {
        private readonly MitsubishiAddressParser _parser = new();
        private volatile bool _connected;

        public PlcBrand Brand => PlcBrand.MitsubishiMc3E;
        public PlcCapabilities Capabilities => PlcCapabilities.MitsubishiMc3E;
        public bool IsConnected => _connected;

        public bool TryParseAddress(string text, out PlcAddress address) => _parser.TryParse(text, out address);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _connected = false;
            return Task.CompletedTask;
        }

        public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return [];
        }

        public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// R07 回归：队列停机时，正在执行/排队中的请求必须收到明确的取消结局，
    /// 而不是让等待方（无取消令牌的调用方）永久悬挂。
    /// </summary>
    [Fact]
    public async Task Queue_dispose_cancels_pending_requests_instead_of_hanging_them()
    {
        var queue = new PlcRequestQueue(new HangingPlcDriver());
        Assert.True(queue.Driver.TryParseAddress("D100", out var address));

        // 该读会挂到执行被取消为止；调用方特意不带取消令牌 —— 复现"停机后等待方悬挂"的现场。
        var pendingCall = queue.ReadWordsAsync(address, 1, CancellationToken.None);
        await Task.Delay(200); // 让请求进入队列执行

        await queue.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pendingCall.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// 回写发生不确定失败时保持原触发与持久人工留证，停止服务不得再自动重写。
    /// </summary>
    [Fact]
    public async Task StopAsync_keeps_single_write_failure_blocked_for_manual_review()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        await using var ctx = new CollectionHostedServiceTests.HarnessServiceContext(harness);
        using var cts = new CancellationTokenSource();
        await ((IHostedService)ctx.Service).StartAsync(cts.Token);
        try
        {
            // 新进程必须先在真实扫描中确认 trigger 非触发；模拟器初始化也需要约 800ms。
            await CollectionHostedServiceTests.WaitUntilAsync(
                () => harness.StatusHub.CollectionReadiness.Ready &&
                      ctx.Service.Queues.Count > 0 &&
                      ctx.Service.HasArmedStartupTrigger(station.Id));

            LoadCycle(harness, station, "P0500");
            // PLC 拒绝单次结果写：这条事件必须进入人工核对，不做自动重写。
            harness.Simulator.SetFailWrites(true);

            // 等单次写失败（留证已随落库登记）。
            await CollectionHostedServiceTests.WaitUntilAsync(() => ctx.Logged("不自动重写"));
        }
        finally
        {
            cts.Cancel();
            await ((IHostedService)ctx.Service).StopAsync(CancellationToken.None);
        }

        // 停机后触发位仍在，未确认记录持久进入人工核对。
        Assert.Equal(ResultCodes.Trigger, (short)harness.Simulator.GetWord(station.TriggerAddress));
        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        Assert.Equal("P0500", pending.PalletCode);
        Assert.Equal(ResultCodes.Success, pending.ResultCode);
        Assert.True(pending.ManualReviewRequired);
    }

    /// <summary>
    /// F01 门禁：Infrastructure 容器必须能在 ValidateOnBuild + ValidateScopes 下构建。
    /// SessionIndexRelay 这类对账件一旦又把 Scoped 的 IActiveSessionStore 捕获进 Singleton，
    /// 真实宿主在 builder.Build 阶段就会炸 —— 替身容器跑得起来不代表真实宿主起得来。
    /// </summary>
    [Fact]
    public void Infrastructure_container_passes_scope_validation()
    {
        using var workspace = new TempWorkspace();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddDataTraceInfrastructure(workspace.Root);
        // 真实宿主（HostApplicationBuilder）会注册 IHostApplicationLifetime；裸容器要补上
        // 这个替身，否则 Quartz 的 hosted service 在 ValidateOnBuild 阶段的失败与 F01 无关。
        services.AddSingleton<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostApplicationLifetime>(sp =>
            sp.GetRequiredService<Microsoft.Extensions.Hosting.Internal.ApplicationLifetime>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    /// <summary>
    /// F02 回归：走<b>真实</b> RuntimeStore.SaveAsync 产生意图（提交后索引更新失败，意图留存），
    /// 载荷里的 SessionId 必须已是实际会话 Id；随后新作用域重放必须把索引补回来。
    /// 上轮的测试手工填了 SessionId，恰好绕开了生成载荷的失败点 —— 这条补上。
    /// </summary>
    [Fact]
    public async Task SaveAsync_persists_intent_with_real_session_id_and_replays_after_index_failure()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(Path.Combine(workspace.Root, "runtime"));
        var curves = new CurveFileStore(Path.Combine(workspace.Root, "curves"));
        var failing = new FailingActiveSessionStore();
        var store = new RuntimeStore(factory, curves, failing);
        var monthKey = RuntimeDbFactory.MonthKey(DateTime.Now);
        var request = new CollectSaveRequest
        {
            MonthKey = monthKey,
            Record = new CollectRecord
            {
                PalletCode = "P0200",
                SerialNo = "SN-P0200",
                StationId = 10,
                StationCode = "ST010",
                TriggerTime = DateTime.Now,
                Judgement = Judgement.Ok
            },
            UpsertSession = new PalletSession
            {
                SerialNo = "SN-P0200",
                PalletCode = "P0200",
                StartTime = DateTime.Now,
                Status = SessionStatus.Open
            },
            ActiveSession = new ActiveSessionIndex
            {
                PalletCode = "P0200",
                SerialNo = "SN-P0200",
                MonthKey = monthKey,
                StartTime = DateTime.Now,
                RecipeCode = "",
                LastStationId = 10,
                LastStationCode = "ST010",
                LastActivityAt = DateTime.Now
            }
        };

        // 提交成功、config.db 索引更新失败：意图留在月库里（正是硬退出窗口留下的那种状态）。
        await Assert.ThrowsAnyAsync<PostCommitStateUpdateException>(
            () => store.SaveAsync(request));

        var sessionId = request.Record.PalletSessionId;
        Assert.True(sessionId > 0);
        await using (var db = factory.Open(monthKey))
        {
            var intent = await db.SessionIndexIntents.AsNoTracking().SingleAsync();
            // 与 RuntimeStore 序列化同一套 Web 命名策略，否则 camelCase 键匹配不上 PascalCase 属性。
            var payload = JsonSerializer.Deserialize<ActiveSessionIndex>(intent.PayloadJson, IntentJsonOptions)!;
            Assert.Equal(sessionId, payload.SessionId);
        }

        // 新作用域重放：真实配置库索引缺失 → 必须补回（SerialNo 反查兜底也能命中）。
        await using var configDb = await TestDatabase.CreateConfigAsync(Path.Combine(workspace.Root, "config.db"));
        var realSessions = new ActiveSessionStore(configDb);
        Assert.Null(await realSessions.FindByPalletAsync("P0200"));

        var relay = new SessionIndexRelay(factory, realSessions, NullLogger<SessionIndexRelay>.Instance);
        Assert.Equal(1, await relay.RelayPendingAsync(100));

        var restored = await realSessions.FindByPalletAsync("P0200");
        Assert.NotNull(restored);
        Assert.Equal("SN-P0200", restored.SerialNo);
        Assert.Equal(sessionId, restored.SessionId);
    }

    /// <summary>
    /// F02 回归：载荷损坏的意图必须<b>暂缓</b>（AppliedAt 保持空、每轮重试并留诊断），
    /// 不能被当作"过期"作废 —— 作废等于永久丢掉这次补偿机会。
    /// </summary>
    [Fact]
    public async Task Index_intent_with_broken_payload_is_deferred_not_voided()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        LoadCycle(harness, station, "P0104");
        Assert.Equal(ResultCodes.Success, await harness.RunAsync(station));
        var monthKey = Assert.Single((await harness.QueryAsync("P0104")).Items).MonthKey;

        var factory = harness.Provider.GetRequiredService<RuntimeDbFactory>();
        await using (var db = factory.Open(monthKey))
        {
            // 托盘码独立于真实在制索引（否则"被新流水接管"的作废检查会先于载荷解析触发）。
            db.SessionIndexIntents.Add(new SessionIndexIntent
            {
                PalletCode = "P-BROKEN",
                SerialNo = "SN-BROKEN",
                MonthKey = monthKey,
                Operation = 0,
                PayloadJson = "{ 这不是合法 JSON",
                CreatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
        }

        var relay = harness.Provider.GetRequiredService<ISessionIndexRelay>();
        await relay.RelayPendingAsync(100);

        await using (var db = factory.Open(monthKey))
        {
            var intent = await db.SessionIndexIntents.AsNoTracking()
                .SingleAsync(x => x.SerialNo == "SN-BROKEN");
            Assert.Null(intent.AppliedAt);
        }
    }

    /// <summary>
    /// F03 回归：留证 Id 只在月库内唯一，两个月库各留一条（自增后都是 Id=1），
    /// 删除与退避更新必须只命中指定月份的那一条 —— 旧件的防重复保护不能被误删。
    /// </summary>
    [Fact]
    public async Task Pending_write_back_operations_respect_month_identity()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(Path.Combine(workspace.Root, "runtime"));
        var store = new PendingWriteBackStore(factory);

        var oldEvent = new PendingWriteBack
        {
            CollectRecordId = 77,
            PalletCode = "P0300", SerialNo = "SN-OLD", StationId = 1, StationCode = "ST010",
            MonthKey = "202609", PalletSessionId = 1, ResultCode = ResultCodes.Success,
            CreatedAt = DateTime.Now.AddDays(-35), NextAttemptAt = DateTime.Now
        };
        var newEvent = new PendingWriteBack
        {
            CollectRecordId = 77,
            PalletCode = "P0301", SerialNo = "SN-NEW", StationId = 2, StationCode = "ST020",
            MonthKey = "202610", PalletSessionId = 1, ResultCode = ResultCodes.QualityRejected,
            CreatedAt = DateTime.Now, NextAttemptAt = DateTime.Now
        };
        await store.SaveAsync(oldEvent);
        await store.SaveAsync(newEvent);

        // 复现跨月同 Id 的现场形态：两个月份库的第一条自增 Id 相同。
        Assert.Equal(oldEvent.Id, newEvent.Id);

        // 记录 ID 也可能跨月复用：必须同时匹配会话月库和 recordId。
        await store.DeleteByRecordIdAsync("202609", 77);
        Assert.Null(await store.FindOldestUnconfirmedAsync(1));

        var survivor = await store.FindOldestUnconfirmedAsync(2);
        Assert.NotNull(survivor);
        Assert.Equal("SN-NEW", survivor.SerialNo);

        // 退避更新同样只动 202610 的那条。
        await store.RecordFailedAttemptAsync("202610", newEvent.Id, 3, DateTime.Now.AddMinutes(1));
        var updated = await store.FindOldestUnconfirmedAsync(2);
        Assert.NotNull(updated);
        Assert.Equal(3, updated.Attempts);
        Assert.True(updated.NextAttemptAt > DateTime.Now);
    }

    /// <summary>
    /// F06 门禁：运行库模型与迁移链同步（手工 snapshot 漏列/多列在这里红）；
    /// 且"RuntimeSchema v3 已建表 + 迁移历史落后"的库再跑迁移必须幂等，不撞 duplicate table。
    /// </summary>
    [Fact]
    public async Task Runtime_model_matches_migrations_and_table_upgrade_is_idempotent()
    {
        using var workspace = new TempWorkspace();
        var factory = new RuntimeDbFactory(Path.Combine(workspace.Root, "runtime"));
        await using (var db = factory.Open("202610"))
        {
            Assert.False(db.Database.HasPendingModelChanges());
        }

        var legacyFactory = new RuntimeDbFactory(Path.Combine(workspace.Root, "legacy"));
        await using (var db = legacyFactory.Open("202608"))
        {
            // 模拟 RuntimeSchema v3 先行的老库：新表由补丁建、迁移历史回退到新迁移之前。
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"PendingWriteBacks\"");
            await db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"SessionIndexIntents\"");
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE MigrationId = '20261011153000_AddSessionIndexIntentsAndPendingWriteBacks'");
            // RuntimeSchema 是版本化的：把版本退回 2 才会重放 v3 补丁（真实老库从未跑过 v3）。
            await db.Database.ExecuteSqlRawAsync("UPDATE \"RuntimeSchemaVersion\" SET \"Version\" = 2");
            RuntimeSchema.Upgrade(db);
            var intentColumns = await db.Database.SqlQueryRaw<string>(
                "SELECT name AS Value FROM pragma_table_info('SessionIndexIntents')").ToListAsync();
            Assert.Contains("Id", intentColumns);
        }

        // 再开一个新 factory：正常迁移链上重放新迁移（IF NOT EXISTS）必须安静通过，两表可用。
        var freshFactory = new RuntimeDbFactory(Path.Combine(workspace.Root, "legacy"));
        await using (var db = freshFactory.Open("202608"))
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(0, await db.SessionIndexIntents.CountAsync());
            Assert.Equal(0, await db.PendingWriteBacks.CountAsync());
        }
    }
}
