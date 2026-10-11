using System.Collections.Concurrent;
using DataTrace.Application.Configuration;
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

namespace DataTrace.Tests;

public sealed class StartupTriggerGateTests
{
    private const string StartupGateError = "启动时触发已有效";

    [Fact]
    public async Task Active_trigger_at_process_start_is_held_until_inactive_then_new_active_is_collected()
    {
        await using var harness = await CreateHarnessAsync();
        var (station, plc) = await PrepareSingleStationLineAsync(harness);
        const string pallet = "P-STARTUP-GATE-1";
        SimulatorScenario.LoadStationCycle(harness.Simulator, plc, station, pallet,
            new SimulatedCycleOptions { Random = new Random(31) });
        var factory = Assert.IsType<ObservingDriverFactory>(harness.Provider.GetRequiredService<IPlcDriverFactory>());
        var service = new CollectionHostedServiceTests.HarnessServiceContext(harness).Service;

        using var stopping = new CancellationTokenSource();
        await service.StartAsync(stopping.Token);
        try
        {
            var statusHub = harness.StatusHub;
            var driver = await WaitForDriverAsync(factory);
            await WaitUntilAsync(
                () => StartupBlocked(statusHub, station.Id),
                () => harness.Logs.Dump());

            Assert.Equal((ushort)station.TriggerValue, harness.Simulator.GetWord(station.TriggerAddress));
            Assert.False(driver.WroteAt(station.TriggerAddress), "startup gate must not acknowledge an unowned trigger");
            Assert.False(driver.ReadAny(MeasurementAddresses(station)), "startup gate must not acquire pallet, tag, or curve data");
            Assert.Empty((await harness.QueryAsync(pallet)).Items);

            // The gate arms only after an actual scan observes inactive. Wait for its own fault
            // to clear, then assert a fresh event using the already loaded measurement payload.
            harness.Simulator.SetWord(station.TriggerAddress, 0);
            await WaitUntilAsync(
                () => !StartupBlocked(statusHub, station.Id),
                () => harness.Logs.Dump());
            SimulatorScenario.LoadStationCycle(harness.Simulator, plc, station, pallet,
                new SimulatedCycleOptions { Random = new Random(31) });

            await WaitUntilAsync(
                () => Collected(statusHub, station.Id, pallet) && driver.WroteAt(station.TriggerAddress),
                () => harness.Logs.Dump());
            Assert.Equal((ushort)ResultCodes.Success, harness.Simulator.GetWord(station.TriggerAddress));
            var records = (await harness.QueryAsync(pallet)).Items;
            Assert.Single(records, x => x.Record.StationId == station.Id && x.Record.ResultCode == ResultCodes.Success);
            Assert.True(driver.ReadAny(MeasurementAddresses(station)), "the re-armed event should acquire measurements");
        }
        finally
        {
            stopping.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Changing_trigger_target_to_active_address_requires_a_new_inactive_observation()
    {
        await using var harness = await CreateHarnessAsync();
        var (station, plc) = await PrepareSingleStationLineAsync(harness);
        var oldTrigger = station.TriggerAddress;
        var factory = Assert.IsType<ObservingDriverFactory>(harness.Provider.GetRequiredService<IPlcDriverFactory>());
        var service = new CollectionHostedServiceTests.HarnessServiceContext(harness).Service;
        const string pallet = "P-STARTUP-GATE-2";

        using var stopping = new CancellationTokenSource();
        await service.StartAsync(stopping.Token);
        try
        {
            var statusHub = harness.StatusHub;
            var driver = await WaitForDriverAsync(factory);
            await WaitUntilAsync(() => driver.ReadAt(oldTrigger), () => harness.Logs.Dump());

            var repository = harness.Scope.ServiceProvider.GetRequiredService<IConfigRepository>();
            var updated = (await repository.GetStationsAsync()).Single(x => x.Id == station.Id);
            updated.TriggerAddress = "D60000";
            // New address is already active before the service can observe its new target.
            SimulatorScenario.LoadStationCycle(harness.Simulator, plc, updated, pallet,
                new SimulatedCycleOptions { Random = new Random(47) });
            var newTrigger = updated.TriggerAddress;
            await repository.SaveStationAsync(updated);

            await WaitUntilAsync(() => StartupBlocked(statusHub, station.Id), () => harness.Logs.Dump());
            Assert.Equal((ushort)updated.TriggerValue, harness.Simulator.GetWord(newTrigger));
            Assert.False(driver.WroteAt(newTrigger), "a changed active target must be held, not acknowledged");
            Assert.False(driver.ReadAny(MeasurementAddresses(updated)), "a changed active target must not read measurement data");
            Assert.Empty((await harness.QueryAsync(pallet)).Items);

            SimulatorScenario.LoadStationCycle(harness.Simulator, plc, updated, pallet,
                new SimulatedCycleOptions { Random = new Random(47) });
            harness.Simulator.SetWord(newTrigger, 0);
            await WaitUntilAsync(() => !StartupBlocked(statusHub, station.Id), () => harness.Logs.Dump());
            SimulatorScenario.LoadStationCycle(harness.Simulator, plc, updated, pallet,
                new SimulatedCycleOptions { Random = new Random(47) });

            await WaitUntilAsync(
                () => Collected(statusHub, station.Id, pallet) && driver.WroteAt(newTrigger),
                () => harness.Logs.Dump());
            Assert.Equal((ushort)ResultCodes.Success, harness.Simulator.GetWord(newTrigger));
            var records = (await harness.QueryAsync(pallet)).Items;
            Assert.Single(records, x => x.Record.StationId == station.Id && x.Record.ResultCode == ResultCodes.Success);
            Assert.False(driver.WroteAt(oldTrigger));
        }
        finally
        {
            stopping.Cancel();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<CollectHarness> CreateHarnessAsync()
        => await CollectHarness.CreateAsync(configureServices: services =>
            services.AddSingleton<IPlcDriverFactory, ObservingDriverFactory>());

    private static async Task<(Station Station, PlcConnection Plc)> PrepareSingleStationLineAsync(CollectHarness harness)
    {
        var repository = harness.ConfigRepository;
        var stations = await repository.GetStationsAsync();
        var first = stations.Single(x => x.IsFirstStation);
        first.IsLastStation = true;
        foreach (var other in stations.Where(x => x.Id != first.Id))
        {
            other.Enabled = false;
            await repository.SaveStationAsync(other);
        }
        await repository.SaveStationAsync(first);

        var connection = (await repository.GetPlcConnectionsAsync()).Single(x => x.Id == first.PlcConnectionId);
        connection.MergeGapWords = 0;
        await repository.SavePlcConnectionAsync(SavePlcConnectionCommand.From(connection));

        var snapshot = await repository.GetSnapshotAsync();
        return (snapshot.Stations.Single(x => x.Id == first.Id),
            snapshot.PlcConnections.Single(x => x.Id == connection.Id));
    }

    private static async Task<ObservingDriver> WaitForDriverAsync(ObservingDriverFactory factory)
    {
        await WaitUntilAsync(() => factory.Driver is not null);
        return factory.Driver!;
    }

    private static bool StartupBlocked(IRuntimeStatusHub status, int stationId)
        => status.Stations.Any(x => x.StationId == stationId
            && x.State == StationRuntimeState.Fault
            && x.LastError?.Contains(StartupGateError, StringComparison.Ordinal) == true);

    private static bool Collected(IRuntimeStatusHub status, int stationId, string pallet)
        => status.Stations.Any(x => x.StationId == stationId
            && x.LastPalletCode == pallet
            && x.LastResultCode == ResultCodes.Success);

    private static string[] MeasurementAddresses(Station station)
        => [station.PalletCodeAddress,
            .. station.Positions.Where(x => !string.IsNullOrWhiteSpace(x.OccupiedAddress)).Select(x => x.OccupiedAddress!),
            .. station.Tags.Where(x => x.Enabled && x.Source == TagDataSource.Plc).Select(x => x.Address),
            .. station.Curves.Where(x => x.Enabled).SelectMany(x => x.Series).Select(x => x.StartAddress)];

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? diagnostics = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("启动触发门禁测试等待超时。" + (diagnostics?.Invoke() ?? ""));
            await Task.Delay(20);
        }
    }

    private sealed class ObservingDriverFactory(SimulatorCatalog simulators) : IPlcDriverFactory
    {
        private ObservingDriver? _driver;
        public ObservingDriver? Driver => Volatile.Read(ref _driver);

        public IPlcDriver Create(PlcConnection connection)
        {
            var driver = new ObservingDriver(simulators.Get(connection.Id));
            Volatile.Write(ref _driver, driver);
            return driver;
        }
    }

    private sealed class ObservingDriver(InMemoryPlcDriver inner) : IPlcDriver
    {
        private readonly ConcurrentQueue<(PlcAddress Address, int Count)> _reads = new();
        private readonly ConcurrentQueue<PlcAddress> _writes = new();

        public PlcBrand Brand => inner.Brand;
        public PlcCapabilities Capabilities => inner.Capabilities;
        public bool IsConnected => inner.IsConnected;

        public bool TryParseAddress(string text, out PlcAddress address) => inner.TryParseAddress(text, out address);
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);

        public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
        {
            _reads.Enqueue((start, wordCount));
            return await inner.ReadWordsAsync(start, wordCount, cancellationToken).ConfigureAwait(false);
        }

        public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
        {
            _writes.Enqueue(start);
            return inner.WriteWordsAsync(start, words, cancellationToken);
        }

        public bool ReadAt(string address) => HasRead([address]);
        public bool ReadAny(IEnumerable<string> addresses) => HasRead(addresses);

        public bool WroteAt(string address)
            => TryParseAddress(address, out var target)
               && _writes.Any(write => write.Area == target.Area && write.Offset == target.Offset);

        private bool HasRead(IEnumerable<string> addresses)
        {
            var targets = addresses.Where(a => TryParseAddress(a, out _))
                .Select(a => { TryParseAddress(a, out var parsed); return parsed; })
                .ToArray();
            return _reads.Any(read => targets.Any(target => read.Address.Area == target.Area
                && target.Offset >= read.Address.Offset && target.Offset < read.Address.Offset + read.Count));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
