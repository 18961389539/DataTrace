using System.Collections.Concurrent;
using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
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

        if (!_busy.IsEmpty)
        {
            return;
        }

        foreach (var queue in _queues.Values)
        {
            await queue.DisposeAsync().ConfigureAwait(false);
        }

        _queues.Clear();
        _heartbeats.Clear();

        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled))
        {
            try
            {
                var driver = _driverFactory.Create(plc);
                var owns = plc.Brand != PlcBrand.Simulator;
                var queue = new PlcRequestQueue(driver, owns);
                _queues[plc.Id] = queue;
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
        }

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

        _configVersion = snapshot.Version;
        _logger.LogInformation("已加载配置版本 {Version}，PLC {PlcCount}，工站 {StationCount}",
            snapshot.Version, _queues.Count, snapshot.Stations.Count);
        await Task.CompletedTask;
    }

    private async Task ScanTriggersAsync(AppConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        foreach (var plc in snapshot.PlcConnections.Where(x => x.Enabled))
        {
            if (!_queues.TryGetValue(plc.Id, out var queue))
            {
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
                    var start = new PlcAddress(block.Area, block.StartOffset, -1, AddressKind.Word, $"{block.Area}{block.StartOffset}");
                    buffers[i] = await queue.ReadWordsAsync(start, block.WordCount, cancellationToken).ConfigureAwait(false);
                }

                _status.UpsertPlc(new PlcRuntimeStatus { PlcConnectionId = plc.Id, Name = plc.Name, Connected = true });

                foreach (var station in stations)
                {
                    if (!plan.Items.ContainsKey($"t_{station.Id}"))
                    {
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
                state.LastWrite = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "心跳写入失败 {Plc}", plc.Name);
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
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class HeartbeatState
    {
        public DateTime LastWrite { get; set; }
        public short Value { get; set; }
    }
}
