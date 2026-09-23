using DataTrace.Application.Configuration;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

public sealed class LineSimulatorStatus
{
    public bool Running { get; set; }
    public string? CurrentPallet { get; set; }
    public string? CurrentStation { get; set; }
    public short? LastResultCode { get; set; }
    public string LastMessage { get; set; } = "未启动";
    public int CompletedPallets { get; set; }
    public int CompletedStations { get; set; }
    public DateTime? LastTriggerTime { get; set; }
}

public interface ILineSimulator
{
    LineSimulatorStatus Status { get; }
    event Action? Changed;
    void SetRunning(bool running);
    Task RunOnePalletAsync(string? palletCode = null, CancellationToken cancellationToken = default);
}

public sealed class LineSimulatorHostedService : BackgroundService, ILineSimulator
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SimulatorCatalog _simulators;
    private readonly ILogger<LineSimulatorHostedService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly Random _random = new();
    private int _palletSeq;
    private volatile bool _running;

    public LineSimulatorHostedService(
        IServiceScopeFactory scopeFactory,
        SimulatorCatalog simulators,
        ILogger<LineSimulatorHostedService> logger)
    {
        _scopeFactory = scopeFactory;
        _simulators = simulators;
        _logger = logger;
    }

    public LineSimulatorStatus Status { get; } = new();
    public event Action? Changed;

    public void SetRunning(bool running)
    {
        _running = running;
        Status.Running = running;
        Status.LastMessage = running ? "自动仿真运行中" : "已停止";
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(800, stoppingToken).ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var snapshot = await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
                .GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
            if (snapshot.Settings.SimulatorAutoRun && snapshot.PlcConnections.Any(p => p.Enabled && p.Brand == PlcBrand.Simulator))
            {
                SetRunning(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取仿真配置失败");
        }

        _logger.LogInformation("产线仿真服务已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_running)
                {
                    await Task.Delay(300, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunOnePalletAsync(null, stoppingToken).ConfigureAwait(false);
                var interval = 2500;
                await using (var scope = _scopeFactory.CreateAsyncScope())
                {
                    interval = Math.Max(500, (await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
                        .GetSnapshotAsync(stoppingToken).ConfigureAwait(false)).Settings.SimulatorIntervalMs);
                }

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "产线仿真循环异常");
                Status.LastMessage = ex.Message;
                Changed?.Invoke();
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RunOnePalletAsync(string? palletCode = null, CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var snapshot = await scope.ServiceProvider.GetRequiredService<IConfigRepository>()
                .GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var plcById = snapshot.PlcConnections.ToDictionary(p => p.Id);
            var stations = snapshot.Stations
                .Where(s => s.Enabled
                            && plcById.TryGetValue(s.PlcConnectionId, out var plc)
                            && plc.Enabled
                            && plc.Brand == PlcBrand.Simulator)
                .OrderBy(s => s.Sequence)
                .ToList();
            if (stations.Count == 0)
            {
                Status.LastMessage = "没有启用的模拟 PLC / 工站";
                Changed?.Invoke();
                return;
            }

            var pool = Math.Max(1, snapshot.Settings.SimulatorPalletPool);
            var code = string.IsNullOrWhiteSpace(palletCode)
                ? $"P{(_palletSeq++ % pool) + 1:0000}"
                : palletCode.Trim();
            var injectNg = _random.Next(100) < Math.Clamp(snapshot.Settings.SimulatorNgPercent, 0, 100);
            var ngStationId = injectNg ? stations[_random.Next(stations.Count)].Id : -1;

            Status.CurrentPallet = code;
            Status.LastMessage = injectNg ? $"托盘 {code} 本轮将注入 NG" : $"托盘 {code} 上线";
            Changed?.Invoke();

            var completedAll = true;
            foreach (var station in stations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plcConfig = plcById[station.PlcConnectionId];
                var driver = _simulators.Get(plcConfig.Id);
                if (!driver.IsConnected)
                {
                    await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }

                Status.CurrentStation = station.Code;
                Changed?.Invoke();

                SimulatorScenario.LoadStationCycle(driver, plcConfig, station, code, new SimulatedCycleOptions
                {
                    Random = _random,
                    InjectNg = station.Id == ngStationId
                });
                Status.LastTriggerTime = DateTime.Now;

                var result = await WaitHandshakeAsync(driver, station, cancellationToken).ConfigureAwait(false);
                Status.LastResultCode = result;
                Status.CompletedStations++;
                Status.LastMessage = result is null
                    ? $"{station.Code} 等待上位机回写超时"
                    : $"{station.Code} 回写 {ResultCodes.Describe(result.Value)}";
                Changed?.Invoke();

                if (result is null)
                {
                    completedAll = false;
                    break;
                }

                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }

            Status.CurrentStation = null;
            if (completedAll)
            {
                Status.CompletedPallets++;
                Status.LastMessage = $"托盘 {code} 已走完全线";
            }

            Changed?.Invoke();
        }
        finally
        {
            _runLock.Release();
        }
    }

    private static async Task<short?> WaitHandshakeAsync(InMemoryPlcDriver plc, Station station, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var word = (short)plc.GetWord(station.TriggerAddress);
            if (word != station.TriggerValue)
            {
                return word;
            }

            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
