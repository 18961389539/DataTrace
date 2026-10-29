using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
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

    /// <summary>
    /// 走一托盘（逐站触发并等回写）。返回是否真的走完了 —— 中途超时/没有可跑的工站都会如实返回，
    /// 调用方不能再把"点过按钮"当成"走完了"。
    /// </summary>
    Task<LineRunResult> RunOnePalletAsync(string? palletCode = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 一次走线的结果：<see cref="Completed"/> 为 false 时 <see cref="Message"/> 说明卡在哪、
/// 以及该往哪个方向查（通常是采集是否停用、地址/扫描间隔是否配错）。
/// </summary>
public sealed record LineRunResult(bool Completed, string? StoppedAtStation, bool TimedOut, string Message);

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

                var result = await RunOnePalletAsync(null, stoppingToken).ConfigureAwait(false);
                if (!result.Completed)
                {
                    // 超时/池满/留证阻塞时绝不再循环开新件：否则在制托盘池一满就会复用号并刷会话抢占报警。
                    if (_running)
                    {
                        _logger.LogWarning("自动仿真已暂停：{Message}", result.Message);
                        SetRunning(false);
                        Status.LastMessage = $"自动仿真已暂停：{result.Message}";
                        Changed?.Invoke();
                    }

                    await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
                    continue;
                }

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

    public async Task<LineRunResult> RunOnePalletAsync(string? palletCode = null, CancellationToken cancellationToken = default)
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
                const string message = "没有启用的模拟 PLC / 工站";
                Status.LastMessage = message;
                Changed?.Invoke();
                return new LineRunResult(false, null, false, message);
            }

            var pool = Math.Max(1, snapshot.Settings.SimulatorPalletPool);
            var firstStation = stations.FirstOrDefault(station => station.IsFirstStation);
            if (firstStation is null)
            {
                const string message = "启用的模拟工站里没有首站，无法按产线拓扑仿真";
                Status.LastMessage = message;
                Changed?.Invoke();
                return new LineRunResult(false, null, false, message);
            }

            var beforeFirst = stations.Where(station => station.Sequence < firstStation.Sequence).ToList();
            if (beforeFirst.Count > 0)
            {
                _logger.LogWarning(
                    "仿真跳过首站 {First} 之前的启用工站：{Codes}",
                    firstStation.Code,
                    string.Join(", ", beforeFirst.Select(station => station.Code)));
            }

            var lastStation = stations.LastOrDefault(station => station.IsLastStation);
            var runStations = stations
                .Where(station => station.Sequence >= firstStation.Sequence
                                 && (lastStation is null || station.Sequence <= lastStation.Sequence))
                .ToList();
            if (runStations.Count == 0)
            {
                const string message = "首站到末站之间没有可仿真的启用工站";
                Status.LastMessage = message;
                Changed?.Invoke();
                return new LineRunResult(false, null, false, message);
            }

            // 人工留证阻塞的工站采集不会回写；仿真若继续触发只会 8 秒超时、堆在制会话，最终刷抢占报警。
            var pendingStore = scope.ServiceProvider.GetRequiredService<IPendingWriteBackStore>();
            foreach (var station in runStations)
            {
                var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id, cancellationToken)
                    .ConfigureAwait(false);
                if (pending is { ManualReviewRequired: true })
                {
                    var message =
                        $"工站 {station.Code} 存在待人工核对的回写留证（{pending.SerialNo}），仿真已停止；请在诊断页解除留证后再开自动仿真";
                    Status.LastMessage = message;
                    Changed?.Invoke();
                    return new LineRunResult(false, station.Code, false, message);
                }
            }

            // 避免复用仍在制的托盘号，否则首站会再次触发会话抢占。
            var busyPallets = (await scope.ServiceProvider.GetRequiredService<IActiveSessionStore>()
                    .ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(session => session.PalletCode)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string code;
            if (!string.IsNullOrWhiteSpace(palletCode))
            {
                code = palletCode.Trim();
                if (busyPallets.Contains(code))
                {
                    var message = $"托盘 {code} 仍在制，不能再次从首站进入；请等末站关闭会话或先处理在制件";
                    Status.LastMessage = message;
                    Changed?.Invoke();
                    return new LineRunResult(false, null, false, message);
                }
            }
            else
            {
                var allocated = TryAllocatePalletCode(firstStation, runStations, pool, busyPallets);
                if (allocated is null)
                {
                    var message =
                        $"托盘池（{pool}）内全部在制，拒绝复用以免会话抢占；请检查卡在中途的工站（常见：回写留证阻塞）并清理在制会话";
                    Status.LastMessage = message;
                    Changed?.Invoke();
                    return new LineRunResult(false, null, false, message);
                }

                code = allocated;
            }

            if (firstStation.PalletCodeDataType != PlcDataType.String)
            {
                code = SimulatorScenario.NormalizePalletCode(firstStation, code);
            }

            var injectNg = _random.Next(100) < Math.Clamp(snapshot.Settings.SimulatorNgPercent, 0, 100);
            var ngStationId = injectNg ? runStations[_random.Next(runStations.Count)].Id : -1;

            Status.CurrentPallet = code;
            Status.LastMessage = injectNg
                ? $"托盘 {code} 本轮将注入 NG"
                : beforeFirst.Count > 0
                    ? $"托盘 {code} 上线（已跳过首站前 {beforeFirst.Count} 站）"
                    : $"托盘 {code} 上线";
            Changed?.Invoke();

            var completedAll = true;
            string? stalledAt = null;
            var timedOut = false;
            foreach (var station in runStations)
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
                Status.LastMessage = result is null
                    ? $"{station.Code} 等待上位机回写超时"
                    : $"{station.Code} 回写 {ResultCodes.Describe(result.Value)}";
                Changed?.Invoke();

                if (result is null)
                {
                    completedAll = false;
                    stalledAt = station.Code;
                    timedOut = true;
                    break;
                }

                // 只有真回写了才算走完这一站：超时也计数会让"累计工站"看起来像成功数。
                Status.CompletedStations++;
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            }

            Status.CurrentStation = null;
            if (completedAll)
            {
                Status.CompletedPallets++;
                Status.LastMessage = $"托盘 {code} 已走完全线";
            }

            Changed?.Invoke();

            return completedAll
                ? new LineRunResult(true, null, false, $"托盘 {code} 已走完全线")
                : new LineRunResult(
                    false,
                    stalledAt,
                    timedOut,
                    timedOut
                        ? $"托盘 {code} 在 {stalledAt} 等待上位机回写超时，未走完；请确认采集已启用、该工站是否被回写留证阻塞、地址与扫描间隔是否正确"
                        : $"托盘 {code} 在 {stalledAt} 未走完；请确认采集已启用、该工站地址与扫描间隔是否正确");
        }
        finally
        {
            _runLock.Release();
        }
    }

    private string? TryAllocatePalletCode(
        Station firstStation,
        IReadOnlyList<Station> runStations,
        int pool,
        HashSet<string> busyPallets)
    {
        // 产线混用 String / Int16 托盘地址时，若首站发 P0001、后站被 Normalize 成 1，
        // 会在 ActiveSessions 里拆成两套身份，末站关不掉首站会话，池一复用就会话抢占。
        // 只要本趟有任一非 String 托盘站，就统一发纯数字号，各站都能读成同一身份。
        var useNumeric = runStations.Any(station => station.PalletCodeDataType != PlcDataType.String)
                         || firstStation.PalletCodeDataType != PlcDataType.String;

        for (var attempt = 0; attempt < Math.Max(pool, 1) * 2; attempt++)
        {
            var candidate = useNumeric
                ? ((_palletSeq++ % pool) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"P{(_palletSeq++ % pool) + 1:0000}";
            if (!busyPallets.Contains(candidate))
            {
                return candidate;
            }
        }

        // 池内全忙：宁可停仿真，也不复用在制号去刷「还没走完末站又从首站进入」。
        return null;
    }

    private static async Task<short?> WaitHandshakeAsync(InMemoryPlcDriver plc, Station station, CancellationToken cancellationToken)
    {
        var deadline = DateTime.Now.AddSeconds(8);
        while (DateTime.Now < deadline)
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