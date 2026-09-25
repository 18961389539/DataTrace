using System.Collections.Concurrent;
using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
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

public sealed class CollectionHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlcDriverFactory _driverFactory;
    private readonly StationCollectPipeline _pipeline;
    private readonly IRuntimeStatusHub _status;
    private readonly ILogger<CollectionHostedService> _logger;
    private readonly ConcurrentDictionary<int, byte> _busy = new();
    private readonly Dictionary<int, PlcRequestQueue> _queues = new();
    private readonly Dictionary<int, HeartbeatState> _heartbeats = new();
    private readonly Dictionary<int, string> _signatures = new();
    private readonly WarningThrottle _warnings = new();
    private AppConfigurationSnapshot? _snapshot;
    private int _configVersion = -1;

    public CollectionHostedService(
        IServiceScopeFactory scopeFactory,
        IPlcDriverFactory driverFactory,
        StationCollectPipeline pipeline,
        IRuntimeStatusHub status,
        ILogger<CollectionHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _driverFactory = driverFactory;
        _pipeline = pipeline;
        _status = status;
        _logger = logger;
    }

    internal IReadOnlyDictionary<int, PlcRequestQueue> Queues => _queues;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("采集服务已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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

                await EnsureQueuesAsync(snapshot, stoppingToken).ConfigureAwait(false);
                await ScanTriggersAsync(snapshot, stoppingToken).ConfigureAwait(false);
                await WriteHeartbeatsAsync(snapshot, stoppingToken).ConfigureAwait(false);
                await Task.Delay(Math.Max(20, snapshot.Settings.ScanIntervalMs), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "采集扫描循环异常");
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureQueuesAsync(AppConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
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

        if (obsolete.Count > 0 && !_busy.IsEmpty)
        {
            // 有采集任务正在用这些队列，等它跑完再换，避免把请求丢在半路。
            return;
        }

        // 只重建需要换的连接：其余连接保持原样，不会因为"改了一个点位"就全体断线重连。
        foreach (var id in obsolete)
        {
            if (_queues.Remove(id, out var queue))
            {
                await queue.DisposeAsync().ConfigureAwait(false);
            }

            _heartbeats.Remove(id);
            _signatures.Remove(id);
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
                State = station.Enabled ? StationRuntimeState.Idle : StationRuntimeState.Disabled,
                LastPalletCode = previous?.LastPalletCode,
                LastSerialNo = previous?.LastSerialNo,
                LastResultCode = previous?.LastResultCode,
                LastJudgement = previous?.LastJudgement ?? Judgement.None,
                LastCompleteTime = previous?.LastCompleteTime,
                LastDurationMs = previous?.LastDurationMs,
                LastError = previous?.LastError,
                LastMonthKey = previous?.LastMonthKey,
                LastRecordId = previous?.LastRecordId,
                LastTags = previous?.LastTags ?? [],
                LastCurves = previous?.LastCurves ?? []
            });
        }
    }

    /// <summary>同一个问题最多每分钟记一条：采集循环很快，配置类告警不能每轮都刷。</summary>
    private void WarnSkipped(string key, string message)
    {
        if (_warnings.ShouldWarn(key))
        {
            _logger.LogWarning("{Message}", message);
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

    private async Task ScanTriggersAsync(AppConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled))
        {
            if (!_queues.TryGetValue(plc.Id, out var queue))
            {
                continue;
            }

            if (queue.IsCoolingDown)
            {
                // 刚怀疑断过线：冷却期内不再发起请求，否则每轮都要付出重连超时的代价，
                // 把整条扫描循环拖到远慢于 ScanIntervalMs，其他工站跟着一起慢。
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
                    WarnSkipped($"t_parse_{station.Id}",
                        $"工站 {station.Code} 的触发地址「{station.TriggerAddress}」解析失败，本轮不参与触发判定");
                    continue;
                }

                if (addr.IsBit)
                {
                    // 位地址进不了读计划（只收字地址），以前这里会静默跳过：工站从不触发，现场看不出原因。
                    WarnSkipped($"t_bit_{station.Id}",
                        $"工站 {station.Code} 的触发地址「{station.TriggerAddress}」是位地址，读计划只收字地址，" +
                        "该工站不会被触发；请改用字地址（如 D100，非 0 即触发）");
                    continue;
                }

                if (StationConfigLimits.IsWriteBackCode(station.TriggerValue))
                {
                    // 历史脏配置的兜底：触发值等于回写码时，写完响应码寄存器仍等于触发值，
                    // 会一个扫描周期采一次同一托盘。宁可跳过并说清楚，也不能让它无限循环。
                    WarnSkipped($"t_value_{station.Id}",
                        $"工站 {station.Code} 的触发值 {station.TriggerValue} 落在回写码区间（2–8），" +
                        "触发位永远不会被清掉，该工站已被跳过；请把触发值改回 1（或 2–8 以外的值）");
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
                        WarnSkipped($"t_plan_{station.Id}",
                            $"工站 {station.Code} 的触发地址未进入读计划，本轮跳过；请检查触发地址配置");
                        continue;
                    }

                    var words = plan.GetWords($"t_{station.Id}", buffers);
                    if (words.Length == 0 || (short)words[0] != station.TriggerValue)
                    {
                        continue;
                    }

                    if (!_busy.TryAdd(station.Id, 0))
                    {
                        continue;
                    }

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _pipeline.ExecuteAsync(station, plc, snapshot, queue, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "工站 {Station} 采集任务崩溃", station.Code);
                        }
                        finally
                        {
                            _busy.TryRemove(station.Id, out _);
                        }
                    }, cancellationToken);
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
    }

    private async Task WriteHeartbeatsAsync(AppConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled && x.Heartbeat is { Enabled: true }))
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

            if (DateTime.UtcNow - state.LastWrite < TimeSpan.FromMilliseconds(Math.Max(200, plc.Heartbeat.IntervalMs)))
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
                state.LastWrite = DateTime.UtcNow;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues.Values)
        {
            await queue.DisposeAsync().ConfigureAwait(false);
        }

        _queues.Clear();
        _heartbeats.Clear();
        _signatures.Clear();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class HeartbeatState
    {
        public DateTime LastWrite { get; set; }
        public short Value { get; set; }
    }
}
