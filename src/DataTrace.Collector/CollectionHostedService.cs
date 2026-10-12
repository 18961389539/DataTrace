using System.Collections.Concurrent;
using System.Diagnostics;
using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Validation;
using DataTrace.Plc;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Planning;
using DataTrace.Plc.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

public sealed class CollectionHostedService : BackgroundService, IPlcQueueAccess
{
    private const string StartupTriggerBlockedReason = "启动时触发已有效，需现场核对并使设备先回到非触发状态";
    /// <summary>
    /// 一轮干完到下一轮开始之间至少留出的时间（毫秒）。
    /// </summary>
    /// <remarks>
    /// 按周期调度要扣掉已用的时间，扣到 0 就成了紧密循环。留 1 毫秒既不影响
    /// 最小扫描间隔（20ms）下的准点，又能避免一轮异常快时把 CPU 攥住不放。
    /// </remarks>
    private const int MinLoopIdleMs = 1;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlcDriverFactory _driverFactory;
    private readonly StationCollectPipeline _pipeline;
    private readonly IPendingWriteBackStore _pending;
    private readonly ISpoolStore _spool;
    private readonly IRuntimeStatusHub _status;
    private readonly ICollectorDiagnostics _diagnostics;
    private readonly ILogger<CollectionHostedService> _logger;
    // 在途采集任务表：值是任务本身，停机时能限时汇合（复审 R07：先停循环、汇合在途、再释放队列）。
    private readonly ConcurrentDictionary<int, Task?> _busy = new();
    // 并发字典：采集循环在改（配置变更时重建连接），试读从外部读 —— 普通 Dictionary 在并发读写会抛。
    private readonly ConcurrentDictionary<int, PlcRequestQueue> _queues = new();
    private readonly ConcurrentDictionary<int, HeartbeatState> _heartbeats = new();
    // One polling job per connection. A stalled PLC never holds up another connection's
    // next scan; its own queue remains serial and cannot accumulate polling jobs.
    private readonly ConcurrentDictionary<int, Task<PlcPollRound>> _pollRounds = new();
    private readonly ConcurrentDictionary<int, StartupTriggerGate> _startupTriggerGates = new();
    private readonly object _startupTriggerGateLock = new();
    private long _completedScanMs;
    private long _completedHeartbeatMs;
    private readonly Dictionary<int, string> _signatures = new();
    private readonly WarningThrottle _warnings = new();
    private AppConfigurationSnapshot? _snapshot;
    private int _configVersion = -1;
    private bool _recoveryReady;

    public CollectionHostedService(
        IServiceScopeFactory scopeFactory,
        IPlcDriverFactory driverFactory,
        StationCollectPipeline pipeline,
        IPendingWriteBackStore pending,
        ISpoolStore spool,
        IRuntimeStatusHub status,
        ICollectorDiagnostics diagnostics,
        ILogger<CollectionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _driverFactory = driverFactory;
        _pipeline = pipeline;
        _pending = pending;
        _spool = spool;
        _status = status;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    internal IReadOnlyDictionary<int, PlcRequestQueue> Queues => _queues;

    internal bool HasArmedStartupTrigger(int stationId)
    {
        lock (_startupTriggerGateLock)
        {
            return _startupTriggerGates.TryGetValue(stationId, out var gate) && gate.Armed;
        }
    }

    /// <summary>
    /// 试读用：取该 PLC 现成的队列 —— 队列是这台 PLC 的唯一连接收口，自己新建一个会和扫描抢连接。
    /// </summary>
    /// <remarks>
    /// 取不到是常态而非异常：采集关闭时主循环直接 continue，压根不会建队列。
    /// 所以这里给出可读原因，让页面把按钮禁掉并说清为什么，而不是点下去才失败。
    /// </remarks>
    public bool TryGetTrialQueue(int plcConnectionId, out PlcRequestQueue queue, out string? reason)
    {
        if (_queues.TryGetValue(plcConnectionId, out var found))
        {
            queue = found;
            reason = null;
            return true;
        }

        queue = null!;
        reason = _snapshot is null
            ? "采集尚未读到配置，请稍后再试。"
            : !_snapshot.Settings.CollectEnabled
                ? "采集已关闭：PLC 连接未建立，无法试读。请先开启采集。"
                : "该工站所属的 PLC 尚未建立连接，无法试读。";
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("采集服务已启动");
        await NgStreakStartup.EnsureAsync(_scopeFactory, _status, _logger, stoppingToken).ConfigureAwait(false);

        // First scan waits for complete reconciliation; a Deferred intent is a durable readiness block.
        _recoveryReady = await ReconcileBeforeCollectionAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var roundSw = Stopwatch.StartNew();
            // 这一轮"干活"花了多久，不含随后的等待。null 表示还没记（异常轮次由 catch 补记）。
            long? workMs = null;
            var scanMs = 0L;
            var heartbeatMs = 0L;
            var stats = ScanStats.None;
            var timing = false;
            try
            {
                if (!_recoveryReady)
                {
                    _recoveryReady = await ReconcileBeforeCollectionAsync(stoppingToken).ConfigureAwait(false);
                    if (!_recoveryReady)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }
                _status.NoteCollectorTick();
                await using var scope = _scopeFactory.CreateAsyncScope();
                var configRepo = scope.ServiceProvider.GetRequiredService<IConfigRepository>();
                var version = await configRepo.GetVersionAsync(stoppingToken).ConfigureAwait(false);
                if (_snapshot is null || version != _configVersion)
                {
                    _snapshot = await configRepo.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                }

                var snapshot = _snapshot;
                if (snapshot is null)
                {
                    await Task.Delay(200, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (!snapshot.Settings.CollectEnabled)
                {
                    await Task.Delay(500, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                timing = true;
                await EnsureQueuesAsync(snapshot, stoppingToken).ConfigureAwait(false);

                // EnsureQueuesAsync can defer a connection swap while an old scan/work round
                // drains. Never dispatch the new snapshot through the previous target's queue.
                if (_configVersion != snapshot.Version)
                {
                    await Task.Delay(20, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                stats = await ScanTriggersAsync(snapshot, stoppingToken).ConfigureAwait(false);
                // Network durations come from completed connection rounds, not from the
                // scheduler's short dispatch pass. Heartbeats share each PLC's polling job.
                scanMs = _completedScanMs;
                heartbeatMs = _completedHeartbeatMs;

                // 先取读数，再等。以前是在 finally 里读秒表，而 finally 跑在等待之后 ——
                // 于是"每轮工作耗时"恒等于扫描间隔（实测：平均 200ms、配置 200ms、28 轮超时，
                // 而同一页上扫描与心跳都是 0ms，那 200ms 就是等待本身），
                // "超时"因此永远在报，这一页也就失去了意义。
                workMs = roundSw.ElapsedMilliseconds;

                // 扫描间隔是**周期**（见 HelpTexts.ScanInterval：轮询 PLC 的周期）：扣掉这一轮已经
                // 花掉的时间，而不是干完活再整个等一遍。后者会让实际周期变成"工作 + 间隔"，
                // 工作量一涨，采样就悄悄变慢，而设置页上的数字还写着原值。
                var interval = Math.Max(SettingsLimits.MinScanIntervalMs, snapshot.Settings.ScanIntervalMs);
                var idleMs = Math.Max(interval - workMs.Value, MinLoopIdleMs);
                await Task.Delay(TimeSpan.FromMilliseconds(idleMs), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // 记在 1 秒重试等待之前：否则这一轮的工作量会把那 1 秒一起算进去。
                workMs = roundSw.ElapsedMilliseconds;
                _logger.LogError(ex, "采集扫描循环异常");
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
            finally
            {
                // 放在 finally 里：抛异常的那一轮同样要留痕 —— 恰恰是它最需要被看见。
                // 采集关闭或还没读到配置的轮次不算（timing=false）：那时"这一轮没干活"是设计如此，
                // 记进去会把诊断页的均值拉低，看着像采集变快了。
                if (timing && workMs is { } spent && _snapshot is { } current)
                {
                    // 与诊断页同一份数字，交给监控端聚合出轮次耗时的 P95/P99。
                    CollectMetrics.RecordPollRound(spent);
                    PublishDiagnostics(current, spent, scanMs, heartbeatMs, stats);
                }
            }
        }
    }

    /// <summary>把这一轮的耗时与各 PLC 的请求流水交给诊断页。</summary>
    private void PublishDiagnostics(
        AppConfigurationSnapshot snapshot,
        long workMs,
        long scanMs,
        long heartbeatMs,
        ScanStats stats)
    {
        static PlcExchangeView Map(PlcExchange exchange) => new(
            exchange.At,
            exchange.Kind == PlcExchangeKind.Write,
            exchange.Address,
            exchange.WordCount,
            exchange.DurationMs,
            exchange.Ok,
            exchange.Error,
            exchange.Values);

        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled))
        {
            if (!_queues.TryGetValue(plc.Id, out var queue))
            {
                continue;
            }

            var traffic = queue.Traffic.Snapshot();
            _diagnostics.PublishPlcTraffic(new PlcTrafficView(
                plc.Id,
                plc.Name,
                traffic.TotalCount,
                traffic.FailureCount,
                traffic.LastDurationMs,
                traffic.MaxDurationMs,
                traffic.LastSuccessAt,
                traffic.Recent.Select(Map).ToList(),
                // 失败另存的那一份：否则几十条流水一冲，标题里的"失败 N"就再没有下文。
                traffic.RecentFailures.Select(Map).ToList()));
        }

        _diagnostics.PublishLoopTick(new CollectorLoopTick(
            DateTime.Now,
            snapshot.Settings.ScanIntervalMs,
            workMs,
            scanMs,
            heartbeatMs,
            _queues.Count,
            stats.Triggered,
            stats.CoolingDownPlcs,
            stats.SkippedStations));
    }

    /// <summary>一轮扫描里"该触发但没触发"的分布。数字本身比日志更能说明问题出在哪一类。</summary>
    private readonly record struct ScanStats(int Triggered, int CoolingDownPlcs, int SkippedStations)
    {
        public static ScanStats None { get; } = new(0, 0, 0);
    }

    private async Task EnsureQueuesAsync(AppConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (_configVersion != snapshot.Version)
        {
            PruneStartupTriggerGates(snapshot);
        }

        if (_configVersion == snapshot.Version && _queues.Count > 0)
        {
            return;
        }

        var desired = snapshot.PlcConnections.Where(x => x.Enabled).ToList();
        var signatures = desired.ToDictionary(p => p.Id, PlcConnectionSignature.Of);

        WarnLineTopology(snapshot);

        // 需要拆掉的：已停用/已删除的连接，以及连接参数变了的连接。
        var obsolete = _queues.Keys
            .Where(id => !signatures.TryGetValue(id, out var signature)
                         || _signatures.GetValueOrDefault(id) != signature)
            .ToList();

        if (obsolete.Count > 0 && (!_busy.IsEmpty
            || obsolete.Any(id => _pollRounds.TryGetValue(id, out var round) && !round.IsCompleted)))
        {
            // 有采集任务正在用这些队列，等它跑完再换，避免把请求丢在半路。
            return;
        }

        // 只重建需要换的连接：其余连接保持原样，不会因为"改了一个点位"就全体断线重连。
        foreach (var id in obsolete)
        {
            if (_queues.TryRemove(id, out var queue))
            {
                await queue.DisposeAsync().ConfigureAwait(false);
            }

            _heartbeats.TryRemove(id, out _);
            _pollRounds.TryRemove(id, out _);
            _signatures.Remove(id);
            // 连接没了就把它的流水一起撤掉，否则诊断页会一直挂着一台已经不存在的 PLC。
            _diagnostics.ForgetPlc(id);
        }

        // 需要新建的：还没有队列的连接。签名已记录说明上一轮建过（可能失败），不重复重试。
        var pending = desired
            .Where(p => !_queues.ContainsKey(p.Id) && _signatures.GetValueOrDefault(p.Id) != signatures[p.Id])
            .ToList();

        if (obsolete.Count == 0 && pending.Count == 0)
        {
            if (_configVersion != snapshot.Version)
            {
                // 版本变了但连接本身没变（改的是工站点位/曲线/设置）：连接不动，只认下新版本号。
                _configVersion = snapshot.Version;
                RefreshStationStatuses(snapshot);
                _logger.LogDebug("配置版本 {Version} 变更未影响 PLC 连接，保留现有连接", snapshot.Version);
            }

            return;
        }

        foreach (var plc in pending)
        {
            try
            {
                var driver = _driverFactory.Create(plc);
                var owns = plc.Brand != PlcBrand.Simulator;
                _queues[plc.Id] = new PlcRequestQueue(driver, owns);
                _status.UpsertPlc(new PlcRuntimeStatus
                {
                    PlcConnectionId = plc.Id,
                    Name = plc.Name,
                    Connected = driver.IsConnected
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建 PLC 连接 {Name} 失败", plc.Name);
                _status.UpsertPlc(new PlcRuntimeStatus
                {
                    PlcConnectionId = plc.Id,
                    Name = plc.Name,
                    Connected = false,
                    LastError = ex.Message
                });
            }

            // 无论成败都记下签名：失败的连接等配置再变时重试，而不是每个扫描周期都重来一次。
            _signatures[plc.Id] = signatures[plc.Id];
        }

        RefreshStationStatuses(snapshot);

        _configVersion = snapshot.Version;
        _status.SetActiveRecipe(snapshot.ActiveRecipe?.Code, snapshot.ActiveRecipe?.Name);
        _logger.LogInformation("已加载配置版本 {Version}，PLC {PlcCount}（本轮重建 {Rebuilt} 台），工站 {StationCount}",
            snapshot.Version, _queues.Count, obsolete.Count, snapshot.Stations.Count);
    }

    private void RefreshStationStatuses(AppConfigurationSnapshot snapshot)
    {
        foreach (var station in snapshot.Stations)
        {
            var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
            _status.UpsertStation(new StationRuntimeStatus
            {
                StationId = station.Id,
                StationCode = station.Code,
                StationName = station.Name,
                Sequence = station.Sequence,
                State = !station.Enabled
                    ? StationRuntimeState.Disabled
                    : previous?.State is StationRuntimeState.Busy or StationRuntimeState.Fault
                        ? previous.State
                        : StationRuntimeState.Idle,
                LastPalletCode = previous?.LastPalletCode,
                LastSerialNo = previous?.LastSerialNo,
                LastResultCode = previous?.LastResultCode,
                LastJudgement = previous?.LastJudgement ?? Judgement.None,
                LastCompleteTime = previous?.LastCompleteTime,
                LastPieceGap = previous?.LastPieceGap,
                LastDurationMs = previous?.LastDurationMs,
                LastError = previous?.LastError,
                LastMonthKey = previous?.LastMonthKey,
                LastRecordId = previous?.LastRecordId,
                LastWriteBackAttempts = previous?.LastWriteBackAttempts ?? 0,
                LastWriteBackOk = previous?.LastWriteBackOk ?? false,
                LastTags = previous?.LastTags ?? [],
                LastCurves = previous?.LastCurves ?? []
            });
        }
    }

    private void PruneStartupTriggerGates(AppConfigurationSnapshot snapshot)
    {
        var activeStationIds = snapshot.Stations.Where(x => x.Enabled).Select(x => x.Id).ToHashSet();
        lock (_startupTriggerGateLock)
        {
            foreach (var stationId in _startupTriggerGates.Keys)
            {
                if (!activeStationIds.Contains(stationId))
                {
                    _startupTriggerGates.TryRemove(stationId, out _);
                }
            }
        }
    }

    /// <summary>
    /// New process/target must observe a non-trigger state before treating an active value as a new event.
    /// Returns true while the startup gate blocks an active trigger.
    /// </summary>
    private bool ObserveStartupTrigger(
        Station station,
        PlcConnection plc,
        short observedValue,
        out bool firstActiveObservation)
    {
        firstActiveObservation = false;
        var targetKey = $"{ConfirmationTargetFingerprint.Of(plc)}|{station.TriggerAddress}|{station.TriggerValue}|{station.EventIdAddress}|{station.ConfirmationRequestAddress}|{station.ConfirmationAckAddress}";
        lock (_startupTriggerGateLock)
        {
            _startupTriggerGates.TryGetValue(station.Id, out var gate);
            if (gate is null || gate.TargetKey != targetKey)
            {
                var hadStartupGateFault = gate?.BlockedByStartupGate == true;
                gate = new StartupTriggerGate(targetKey)
                {
                    // A prior startup-gate fault still belongs to this station. If the target
                    // changes and the new target is observed inactive, that observation is
                    // enough to clear only that gate-owned fault.
                    BlockedByStartupGate = hadStartupGateFault
                };
                _startupTriggerGates[station.Id] = gate;
            }

            if (observedValue != station.TriggerValue)
            {
                gate.Armed = true;
                if (gate.BlockedByStartupGate)
                {
                    gate.BlockedByStartupGate = false;
                    ClearStartupTriggerFault(station);
                }

                return false;
            }

            if (gate.Armed)
            {
                return false;
            }

            if (!gate.BlockedByStartupGate)
            {
                gate.BlockedByStartupGate = true;
                firstActiveObservation = true;
            }

            return true;
        }
    }

    private void SetStartupTriggerFault(Station station)
    {
        var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
        _status.UpsertStation(new StationRuntimeStatus
        {
            StationId = station.Id,
            StationCode = station.Code,
            StationName = station.Name,
            Sequence = station.Sequence,
            State = StationRuntimeState.Fault,
            LastPalletCode = previous?.LastPalletCode,
            LastSerialNo = previous?.LastSerialNo,
            LastResultCode = previous?.LastResultCode,
            LastJudgement = previous?.LastJudgement ?? Judgement.None,
            LastCompleteTime = previous?.LastCompleteTime,
            LastPieceGap = previous?.LastPieceGap,
            LastDurationMs = previous?.LastDurationMs,
            LastError = StartupTriggerBlockedReason,
            LastMonthKey = previous?.LastMonthKey,
            LastRecordId = previous?.LastRecordId,
            LastWriteBackAttempts = previous?.LastWriteBackAttempts ?? 0,
            LastWriteBackOk = previous?.LastWriteBackOk ?? false,
            LastTags = previous?.LastTags ?? [],
            LastCurves = previous?.LastCurves ?? []
        });
    }

    private void ClearStartupTriggerFault(Station station)
    {
        var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
        // Only undo the fault this gate itself published. Pending/spool and unrelated faults
        // remain visible until their own recovery path completes.
        if (previous is null || previous.State != StationRuntimeState.Fault || previous.LastError != StartupTriggerBlockedReason)
        {
            return;
        }

        _status.UpsertStation(new StationRuntimeStatus
        {
            StationId = station.Id,
            StationCode = station.Code,
            StationName = station.Name,
            Sequence = station.Sequence,
            State = StationRuntimeState.Idle,
            LastPalletCode = previous.LastPalletCode,
            LastSerialNo = previous.LastSerialNo,
            LastResultCode = previous.LastResultCode,
            LastJudgement = previous.LastJudgement,
            LastCompleteTime = previous.LastCompleteTime,
            LastPieceGap = previous.LastPieceGap,
            LastDurationMs = previous.LastDurationMs,
            LastError = null,
            LastMonthKey = previous.LastMonthKey,
            LastRecordId = previous.LastRecordId,
            LastWriteBackAttempts = previous.LastWriteBackAttempts,
            LastWriteBackOk = previous.LastWriteBackOk,
            LastTags = previous.LastTags,
            LastCurves = previous.LastCurves
        });
    }

    /// <summary>同一个问题最多每分钟记一条：采集循环很快，配置类告警不能每轮都刷。</summary>
    private void WarnSkipped(string key, string message)
    {
        if (_warnings.ShouldWarn(key))
        {
            _logger.LogWarning("{Message}", message);
        }
    }

    private async Task<bool> ReconcileBeforeCollectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var relay = scope.ServiceProvider.GetRequiredService<ISessionIndexRelay>();
            while (true)
            {
                var before = await relay.CountPendingAsync(cancellationToken).ConfigureAwait(false);
                if (before == 0)
                {
                    _status.SetCollectionReadiness(true);
                    return true;
                }
                var applied = await relay.RelayPendingAsync(200, cancellationToken).ConfigureAwait(false);
                var after = await relay.CountPendingAsync(cancellationToken).ConfigureAwait(false);
                if (applied > 0) _logger.LogWarning("启动对账追回 {Count} 条在制索引意图，剩余 {Remaining}", applied, after);
                if (after == 0)
                {
                    _status.SetCollectionReadiness(true);
                    return true;
                }
                if (after >= before)
                {
                    var reason = $"在制索引对账仍有 {after} 条未决意图且本轮无进展，采集保持未就绪";
                    _status.SetCollectionReadiness(false, reason);
                    _logger.LogError("{Reason}", reason);
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _status.SetCollectionReadiness(false, $"在制索引启动对账失败：{ex.Message}");
            _logger.LogError(ex, "在制索引对账失败，采集保持未就绪");
            return false;
        }
    }

    /// <summary>
    /// 产线拓扑自检：首站/末站缺失或重复、顺序冲突都会让采集端表现得很怪
    /// （会话永不关闭、同一托盘多个序列号、误报跳站），这里按配置版本变化记一条日志说清楚。
    /// </summary>
    private void WarnLineTopology(AppConfigurationSnapshot snapshot)
    {
        var enabled = snapshot.Stations.Where(x => x.Enabled).ToList();
        if (enabled.Count == 0)
        {
            return;
        }

        if (enabled.Count(x => x.IsFirstStation) == 0)
        {
            WarnSkipped("line_no_first",
                "配置里没有任何启用的首站：托盘不会建立会话，采集记录会按「未找到在制托盘会话（可能跳站）」处理");
        }

        if (enabled.Count(x => x.IsLastStation) == 0)
        {
            WarnSkipped("line_no_last",
                "配置里没有任何启用的末站：托盘会话永远不会关闭，在制托盘只会增不会减，MES 也不会收到上报");
        }

        foreach (var group in enabled.GroupBy(x => x.Sequence).Where(g => g.Count() > 1))
        {
            WarnSkipped($"line_seq_{group.Key}",
                $"产线顺序 {group.Key} 被多个启用的工站共用（{string.Join("、", group.Select(x => x.Code))}）：" +
                "顺序决定前后站与跳站判定的口径，请改成互不相同");
        }
    }

    private sealed record PlcPollRound(ScanStats Stats, long ScanMs, long HeartbeatMs);

    private Task<ScanStats> ScanTriggersAsync(AppConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        var combined = ScanStats.None;
        _completedScanMs = 0;
        _completedHeartbeatMs = 0;
        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled))
        {
            if (_pollRounds.TryGetValue(plc.Id, out var previous))
            {
                if (!previous.IsCompleted) continue;
                try
                {
                    var round = previous.GetAwaiter().GetResult();
                    combined = new ScanStats(combined.Triggered + round.Stats.Triggered,
                        combined.CoolingDownPlcs + round.Stats.CoolingDownPlcs,
                        combined.SkippedStations + round.Stats.SkippedStations);
                    _completedScanMs = Math.Max(_completedScanMs, round.ScanMs);
                    _completedHeartbeatMs = Math.Max(_completedHeartbeatMs, round.HeartbeatMs);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex) { _logger.LogError(ex, "PLC {Name} 独立轮询异常", plc.Name); }
                _pollRounds.TryRemove(plc.Id, out _);
            }

            if (!_queues.ContainsKey(plc.Id) || cancellationToken.IsCancellationRequested) continue;
            _pollRounds[plc.Id] = Task.Run(async () =>
            {
                var scanWatch = Stopwatch.StartNew();
                var stats = await ScanPlcTriggersAsync(snapshot, plc.Id, cancellationToken).ConfigureAwait(false);
                var scanElapsed = scanWatch.ElapsedMilliseconds;
                var heartbeatWatch = Stopwatch.StartNew();
                await WriteHeartbeatsAsync(snapshot, plc.Id, cancellationToken).ConfigureAwait(false);
                return new PlcPollRound(stats, scanElapsed, heartbeatWatch.ElapsedMilliseconds);
            }, cancellationToken);
        }

        return Task.FromResult(combined);
    }

    private async Task<ScanStats> ScanPlcTriggersAsync(AppConfigurationSnapshot snapshot, int plcConnectionId, CancellationToken cancellationToken)
    {
        var triggered = 0;
        var coolingDown = 0;
        var skipped = 0;

        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled && x.Id == plcConnectionId))
        {
            if (!_queues.TryGetValue(plc.Id, out var queue))
            {
                continue;
            }

            if (queue.IsCoolingDown)
            {
                // 刚怀疑断过线：冷却期内不再发起请求，否则每轮都要付出重连超时的代价，
                // 把整条扫描循环拖到远慢于 ScanIntervalMs，其他工站跟着一起慢。
                coolingDown++;
                continue;
            }

            var stations = snapshot.Stations.Where(s => s.Enabled && s.PlcConnectionId == plc.Id).ToList();
            if (stations.Count == 0)
            {
                continue;
            }

            var requests = new List<AddressReadRequest>();
            foreach (var station in stations)
            {
                if (!queue.Driver.TryParseAddress(station.TriggerAddress, out var addr))
                {
                    skipped++;
                    WarnSkipped($"t_parse_{station.Id}",
                        $"工站 {station.Code} 的触发地址「{station.TriggerAddress}」解析失败，本轮不参与触发判定");
                    continue;
                }

                if (addr.IsBit)
                {
                    // 位地址进不了读计划（只收字地址），以前这里会静默跳过：工站从不触发，现场看不出原因。
                    skipped++;
                    WarnSkipped($"t_bit_{station.Id}",
                        $"工站 {station.Code} 的触发地址「{station.TriggerAddress}」是位地址，读计划只收字地址，" +
                        "该工站不会被触发；请改用字地址（如 D100，非 0 即触发）");
                    continue;
                }

                if (StationConfigLimits.IsWriteBackCode(station.TriggerValue))
                {
                    // 历史脏配置的兜底：触发值等于回写码时，写完响应码寄存器仍等于触发值，
                    // 会一个扫描周期采一次同一托盘。宁可跳过并说清楚，也不能让它无限循环。
                    skipped++;
                    WarnSkipped($"t_value_{station.Id}",
                        $"工站 {station.Code} 的触发值 {station.TriggerValue} 落在回写码区间" +
                        $"（2–{ResultCodes.ArchiveFailed}），触发位永远不会被清掉，该工站已被跳过；" +
                        $"请把触发值改回 1（或 2–{ResultCodes.ArchiveFailed} 以外的值）");
                    continue;
                }

                requests.Add(new AddressReadRequest
                {
                    Key = $"t_{station.Id}",
                    Address = addr,
                    WordCount = 1
                });
            }

            if (requests.Count == 0)
            {
                continue;
            }

            try
            {
                var plan = ReadPlanBuilder.Build(requests, queue.Driver.Capabilities.MaxWordsPerRead, plc.MergeGapWords);
                var buffers = new ushort[plan.Blocks.Count][];
                for (var i = 0; i < plan.Blocks.Count; i++)
                {
                    var block = plan.Blocks[i];
                    buffers[i] = await queue.ReadWordsAsync(block.StartAddress(), block.WordCount, cancellationToken)
                        .ConfigureAwait(false);
                }

                // 仅在连通性/错误态真有变化时 Upsert，避免每次成功扫描都广播 Changed。
                var plcStatus = _status.Plcs.FirstOrDefault(p => p.PlcConnectionId == plc.Id);
                if (plcStatus is null || !plcStatus.Connected || !string.IsNullOrEmpty(plcStatus.LastError))
                {
                    _status.UpsertPlc(new PlcRuntimeStatus { PlcConnectionId = plc.Id, Name = plc.Name, Connected = true });
                }

                foreach (var station in stations)
                {
                    if (!plan.Items.ContainsKey($"t_{station.Id}"))
                    {
                        skipped++;
                        WarnSkipped($"t_plan_{station.Id}",
                            $"工站 {station.Code} 的触发地址未进入读计划，本轮跳过；请检查触发地址配置");
                        continue;
                    }

                    var words = plan.GetWords($"t_{station.Id}", buffers);
                    if (words.Length == 0)
                    {
                        continue;
                    }

                    var observedTrigger = (short)words[0];
                    if (_pipeline.HasHeldPersistence(station.Id))
                    {
                        // The poll round has read the current trigger state. Retry only durable
                        // persistence of the exact held request; never reacquire measurements or ack.
                        await _pipeline.RetryHeldPersistenceAsync(station.Id, observedTrigger, cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (observedTrigger != station.TriggerValue && !DeviceEventProtocol.Enabled(station))
                    {
                        ObserveStartupTrigger(station, plc, observedTrigger, out _);
                        continue;
                    }

                    // 回写未确认的已提交件：设备无事件号时只转人工核对，绝不自动重写或重新采集 —— 重新采集会让首站
                    // 把旧会话标异常、新建流水号，其他站新增重复记录。留证查询发生在触发命中
                    // 之后（每件一次），不在每轮扫描的公共路径上。
                    if (await _spool.HasPendingForStationAsync(station.Id, cancellationToken).ConfigureAwait(false))
                    {
                        var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
                        _status.UpsertStation(new StationRuntimeStatus
                        {
                            StationId = station.Id, StationCode = station.Code, StationName = station.Name,
                            Sequence = station.Sequence, State = StationRuntimeState.Fault,
                            LastPalletCode = previous?.LastPalletCode, LastSerialNo = previous?.LastSerialNo,
                            LastResultCode = ResultCodes.DatabaseWriteFailed,
                            LastJudgement = previous?.LastJudgement ?? Judgement.None,
                            LastCompleteTime = previous?.LastCompleteTime, LastDurationMs = previous?.LastDurationMs,
                            LastError = "该工站存在待补传 spool 事件，补传入库前禁止重新采集",
                            LastMonthKey = previous?.LastMonthKey, LastRecordId = previous?.LastRecordId,
                            LastWriteBackAttempts = previous?.LastWriteBackAttempts ?? 0,
                            LastWriteBackOk = false, LastTags = previous?.LastTags ?? [], LastCurves = previous?.LastCurves ?? []
                        });
                        continue;
                    }

                    var pending = await _pending.FindOldestUnconfirmedAsync(station.Id, cancellationToken)
                        .ConfigureAwait(false);
                    if (pending is not null)
                    {
                        if (pending.ManualReviewRequired)
                        {
                            var previous = _status.Stations.FirstOrDefault(x => x.StationId == station.Id);
                            _status.UpsertStation(new StationRuntimeStatus
                            {
                                StationId = station.Id, StationCode = station.Code, StationName = station.Name,
                                Sequence = station.Sequence, State = StationRuntimeState.Fault,
                                LastPalletCode = pending.PalletCode, LastSerialNo = pending.SerialNo,
                                LastResultCode = pending.ResultCode, LastJudgement = previous?.LastJudgement ?? Judgement.None,
                                LastCompleteTime = previous?.LastCompleteTime, LastDurationMs = previous?.LastDurationMs,
                                LastError = pending.ManualReviewReason ?? "存在待人工核对的设备事件身份留证",
                                LastMonthKey = pending.MonthKey, LastRecordId = pending.CollectRecordId ?? previous?.LastRecordId,
                                LastWriteBackAttempts = pending.Attempts, LastWriteBackOk = false,
                                LastTags = previous?.LastTags ?? [], LastCurves = previous?.LastCurves ?? []
                            });
                            continue;
                        }

                        if (DateTime.Now < pending.NextAttemptAt)
                        {
                            // 指数退避窗口内不动作：回写失败多数是通讯断了，别把重试打成报文风暴。
                            continue;
                        }

                        if (!StartStationWork(station.Id, async () =>
                        {
                            await _pipeline.RetryPendingConfirmationAsync(station, plc, snapshot, queue, pending, cancellationToken).ConfigureAwait(false);
                        })) continue;

                        triggered++;
                        continue;
                    }

                    if (observedTrigger != station.TriggerValue)
                    {
                        ObserveStartupTrigger(station, plc, observedTrigger, out _);
                        continue;
                    }

                    if (ObserveStartupTrigger(station, plc, observedTrigger, out var firstActiveObservation))
                    {
                        if (firstActiveObservation)
                        {
                            SetStartupTriggerFault(station);
                            _logger.LogCritical(
                                "工站 {Station} 启动或目标变更后首次观测到触发值有效；没有待确认证据，保持触发并等待现场回到非触发状态",
                                station.Code);
                        }
                        continue;
                    }

                    if (!StartStationWork(station.Id, async () =>
                    {
                        await _pipeline.ExecuteAsync(station, plc, snapshot, queue, cancellationToken).ConfigureAwait(false);
                    }))
                    {
                        // 上一件还在跑：不计入"跳过"，它不是配置问题，是节拍本身跟不上。
                        continue;
                    }

                    triggered++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描 PLC {Name} 触发位失败", plc.Name);
                _status.UpsertPlc(new PlcRuntimeStatus
                {
                    PlcConnectionId = plc.Id,
                    Name = plc.Name,
                    Connected = false,
                    LastError = ex.Message
                });
            }
        }

        return new ScanStats(triggered, coolingDown, skipped);
    }

    private async Task WriteHeartbeatsAsync(AppConfigurationSnapshot snapshot, int plcConnectionId, CancellationToken cancellationToken)
    {
        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled && x.Id == plcConnectionId && x.Heartbeat is { Enabled: true }))
        {
            if (!_queues.TryGetValue(plc.Id, out var queue) || plc.Heartbeat is null)
            {
                continue;
            }

            if (queue.IsCoolingDown)
            {
                continue;
            }

            if (!_heartbeats.TryGetValue(plc.Id, out var state))
            {
                state = new HeartbeatState();
                _heartbeats[plc.Id] = state;
            }

            if (DateTime.Now - state.LastWrite < TimeSpan.FromMilliseconds(Math.Max(200, plc.Heartbeat.IntervalMs)))
            {
                continue;
            }

            if (!queue.Driver.TryParseAddress(plc.Heartbeat.Address, out var addr))
            {
                WarnSkipped($"hb_parse_{plc.Id}",
                    $"PLC {plc.Name} 的心跳地址「{plc.Heartbeat.Address}」解析失败，心跳未写入");
                continue;
            }

            if (addr.IsBit)
            {
                WarnSkipped($"hb_bit_{plc.Id}",
                    $"PLC {plc.Name} 的心跳地址「{plc.Heartbeat.Address}」是位地址，心跳只支持字地址（如 D100），未写入");
                continue;
            }

            try
            {
                short value;
                if (plc.Heartbeat.Mode == HeartbeatMode.Toggle)
                {
                    state.Value = state.Value == 0 ? (short)1 : (short)0;
                    value = state.Value;
                }
                else
                {
                    state.Value++;
                    value = state.Value;
                }

                await queue.WriteWordsAsync(addr, ValueCodec.EncodeInt16(value), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "心跳写入失败 {Plc}", plc.Name);
            }
            finally
            {
                // 成功失败都推进下次尝试时间：扫描周期远小于心跳周期，
                // 失败时若只在成功时推进，断线的 PLC 会被每个扫描周期重试一次。
                state.LastWrite = DateTime.Now;
            }
        }
    }

    private bool StartStationWork(int stationId, Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = completion.Task;
        if (!_busy.TryAdd(stationId, task)) return false;

        // Install the stable completion task before scheduling. A fast operation can no longer
        // finish/remove a placeholder before its real Task is assigned back into _busy.
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "工站 {StationId} 在途任务崩溃", stationId); }
            finally
            {
                ((ICollection<KeyValuePair<int, Task?>>)_busy).Remove(new KeyValuePair<int, Task?>(stationId, task));
                completion.TrySetResult();
            }
        });
        return true;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 停机顺序（复审 R07）：先停扫描循环（不再发起新采集）→ 限时汇合在途任务（已提交件的
        // 确认/索引收尾有机会完成）→ 最后才释放队列。旧顺序先 Dispose 队列，在途任务的
        // PLC 请求与回写全被腰斩，宿主退出后留下"库已提交、确认没做"的半成品状态。
        try
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("采集循环未在宿主停机期限内退出，继续按当前期限收尾");
        }

        var inflight = _busy.Values.Where(t => t is not null).Select(t => t!)
            .Concat(_pollRounds.Values.Cast<Task>()).ToArray();
        if (inflight.Length > 0)
        {
            var drain = Task.WhenAll(inflight);
            var timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var finished = await Task.WhenAny(drain, timeout).ConfigureAwait(false);
            if (finished != drain)
            {
                _logger.LogWarning("停机等待 {Count} 个在途采集任务超时，继续释放队列", inflight.Length);
            }
        }

        foreach (var queue in _queues.Values)
        {
            try
            {
                await queue.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("PLC 队列释放达到宿主停机期限");
                break;
            }
        }

        _queues.Clear();
        _heartbeats.Clear();
        _pollRounds.Clear();
        _signatures.Clear();
    }

    private sealed class HeartbeatState
    {
        public DateTime LastWrite { get; set; }
        public short Value { get; set; }
    }

    private sealed class StartupTriggerGate(string targetKey)
    {
        public string TargetKey { get; } = targetKey;
        public bool Armed { get; set; }
        public bool BlockedByStartupGate { get; set; }
    }
}
