using DataTrace.Application.Runtime;
using DataTrace.Application.Realtime;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Constants;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Planning;
using DataTrace.Plc.Queue;
using DataTrace.Plc.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using DataTrace.Infrastructure.Persistence;

namespace DataTrace.Tests;

public class DeviceEventProtocolTests
{
    private static Station ProtocolStation() => new()
    {
        Code = "ST1", TriggerAddress = "D10", EventIdAddress = "D9000",
        ConfirmationRequestAddress = "D9010", ConfirmationAckAddress = "D9020"
    };

    [Fact]
    public void Identity_is_uint64_low_word_first_and_zero_is_rejected()
    {
        const long identity = unchecked((long)0xFEDCBA9876543210UL);
        var request = DeviceEventProtocol.Request(identity, ResultCodes.Success);
        Assert.Equal(new ushort[] { 0x3210, 0x7654, 0xBA98, 0xFEDC, 2 }, request);
        Assert.Equal(identity, DeviceEventProtocol.DecodeIdentity(request));
        Assert.Throws<PlcDriverException>(() => DeviceEventProtocol.DecodeIdentity(new ushort[4]));
    }

    [Fact]
    public void Configuration_requires_all_blocks_and_rejects_overlap_in_word_and_byte_addressing()
    {
        var station = ProtocolStation();
        Assert.Null(DeviceEventProtocol.ConfigurationError(station, PlcBrand.Simulator));
        station.ConfirmationAckAddress = "";
        Assert.NotNull(DeviceEventProtocol.ConfigurationError(station, PlcBrand.Simulator));
        station.ConfirmationAckAddress = "D9002";
        Assert.NotNull(DeviceEventProtocol.ConfigurationError(station, PlcBrand.Simulator));
        station.EventIdAddress = "DB1.DBW0";
        station.ConfirmationRequestAddress = "DB1.DBW8";
        station.ConfirmationAckAddress = "DB1.DBW16";
        station.TriggerAddress = "DB1.DBW40";
        Assert.NotNull(DeviceEventProtocol.ConfigurationError(station, PlcBrand.SiemensS7));
    }

    [Fact]
    public async Task Matching_persistent_ack_proves_old_event_without_writing_new_event()
    {
        var station = ProtocolStation();
        await using var driver = new ProtocolDriver(new InMemoryPlcDriver(), station);
        driver.SetEvent(42);
        driver.Memory.SetWords(station.ConfirmationAckAddress, DeviceEventProtocol.Request(41, ResultCodes.Success));
        await using var queue = new PlcRequestQueue(driver);
        Assert.True(await DeviceEventProtocol.ConfirmAsync(queue, station, 41, ResultCodes.Success, CancellationToken.None));
        Assert.Equal(0, driver.RequestWrites);
        Assert.Equal((ushort)1, driver.Memory.GetWord(station.TriggerAddress));
    }

    [Fact]
    public async Task Changed_event_without_ack_never_writes_confirmation()
    {
        var station = ProtocolStation();
        await using var driver = new ProtocolDriver(new InMemoryPlcDriver(), station);
        driver.SetEvent(42);
        await using var queue = new PlcRequestQueue(driver);
        Assert.False(await DeviceEventProtocol.ConfirmAsync(queue, station, 41, ResultCodes.Success, CancellationToken.None));
        Assert.Equal(0, driver.RequestWrites);
    }

    [Fact]
    public async Task Plc_rejects_event_changed_between_identity_read_and_request_write()
    {
        var station = ProtocolStation();
        await using var driver = new ProtocolDriver(new InMemoryPlcDriver(), station) { ChangeBeforeWrite = 42 };
        driver.SetEvent(41);
        await using var queue = new PlcRequestQueue(driver);
        Assert.False(await DeviceEventProtocol.ConfirmAsync(queue, station, 41, ResultCodes.Success, CancellationToken.None));
        Assert.Equal(0, driver.AcceptedCount);
        Assert.Equal((ushort)1, driver.Memory.GetWord(station.TriggerAddress));
    }

    [Fact]
    public async Task Committed_event_identity_is_frozen_and_delayed_ack_recovery_does_not_recollect()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.EventIdAddress = "D9000";
        station.ConfirmationRequestAddress = "D9010";
        station.ConfirmationAckAddress = "D9020";
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, "P-EVENT", new SimulatedCycleOptions { Random = new Random(7) });
        await using var driver = new ProtocolDriver(harness.Simulator, station) { AcceptRequests = false };
        driver.SetEvent(55);
        await using var queue = new PlcRequestQueue(driver);
        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, queue, CancellationToken.None);
        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        Assert.Equal(55, pending!.DeviceEventId);
        Assert.False(pending.ManualReviewRequired);
        Assert.Equal(station.EventIdAddress, pending.EventIdAddress);
        var first = await harness.QueryAsync("P-EVENT");
        Assert.Equal(1, first.Total);

        // PLC completed the original request but its reply reached the application only after restart/retry.
        driver.Memory.SetWords(station.ConfirmationAckAddress, DeviceEventProtocol.Request(55, pending.ResultCode));
        driver.SetEvent(56);
        var writesBefore = driver.RequestWrites;
        Assert.True(await harness.Pipeline.RetryPendingConfirmationAsync(station, harness.Plc, harness.Snapshot, queue, pending, CancellationToken.None));
        Assert.Null(await pendingStore.FindOldestUnconfirmedAsync(station.Id));
        Assert.Equal(writesBefore, driver.RequestWrites);
        Assert.Equal(1, (await harness.QueryAsync("P-EVENT")).Total);
        Assert.Empty(harness.Provider.GetRequiredService<ICollectorDiagnostics>().ActiveCollections);
    }

    [Fact]
    public async Task Successful_condition_confirmation_removes_exact_pending_record()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var station = harness.Station(0);
        station.EventIdAddress = "D9000";
        station.ConfirmationRequestAddress = "D9010";
        station.ConfirmationAckAddress = "D9020";
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, "P-ACK", new SimulatedCycleOptions { Random = new Random(7) });
        await using var driver = new ProtocolDriver(harness.Simulator, station);
        driver.SetEvent(77);
        await using var queue = new PlcRequestQueue(driver);
        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, queue, CancellationToken.None);
        Assert.Equal(1, driver.AcceptedCount);
        Assert.Null(await harness.Provider.GetRequiredService<IPendingWriteBackStore>().FindOldestUnconfirmedAsync(station.Id));
    }

    [Fact]
    public async Task Spool_persisted_event_confirms_original_result_then_allows_next_event()
    {
        var firstWrite = new FailFirstWrite();
        await using var harness = await CollectHarness.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<ICollectWriter>();
            services.AddSingleton(firstWrite);
            services.AddScoped<ICollectWriter>(sp => new FailOnceCollectWriter(
                sp.GetRequiredService<RuntimeStore>(), sp.GetRequiredService<FailFirstWrite>()));
        });
        var station = harness.Station(0);
        station.EventIdAddress = "D9000";
        station.ConfirmationRequestAddress = "D9010";
        station.ConfirmationAckAddress = "D9020";
        SimulatorScenario.LoadStationCycle(harness.Simulator, harness.Plc, station, "P-SPOOL", new SimulatedCycleOptions { Random = new Random(7) });
        await using var driver = new ProtocolDriver(harness.Simulator, station);
        driver.SetEvent(100);
        await using var queue = new PlcRequestQueue(driver);

        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, queue, CancellationToken.None);

        var spool = harness.Provider.GetRequiredService<ISpoolStore>();
        var originalRequest = Assert.Single(await spool.ListAsync());
        Assert.Equal(originalRequest.Request.Record.ResultCode, (short)driver.Memory.GetWord("D9024"));
        Assert.NotEqual(ResultCodes.DatabaseWriteFailed, originalRequest.Request.Record.ResultCode);
        var replay = new SpoolReplayRunner(
            harness.Provider.GetRequiredService<IServiceScopeFactory>(), spool, NullLogger<SpoolReplayRunner>.Instance);
        Assert.Equal(1, await replay.ReplayAllAsync());
        var pendingStore = harness.Provider.GetRequiredService<IPendingWriteBackStore>();
        var pending = await pendingStore.FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        Assert.Equal(originalRequest.Request.Record.ResultCode, pending!.ResultCode);

        driver.SetEvent(101);
        Assert.True(await harness.Pipeline.RetryPendingConfirmationAsync(
            station, harness.Plc, harness.Snapshot, queue, pending, CancellationToken.None));
        Assert.Null(await pendingStore.FindOldestUnconfirmedAsync(station.Id));
        await harness.Pipeline.ExecuteAsync(station, harness.Plc, harness.Snapshot, queue, CancellationToken.None);
        Assert.Equal(2, (await harness.QueryAsync("P-SPOOL")).Total);
        Assert.Equal(2, driver.AcceptedCount);
    }

    private sealed class FailFirstWrite
    {
        public int Calls;
    }

    private sealed class FailOnceCollectWriter(RuntimeStore store, FailFirstWrite state) : ICollectWriter
    {
        public Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref state.Calls) == 1)
                throw new IOException("simulated first month-db write failure");
            return store.SaveAsync(request, cancellationToken);
        }

        public Task RepairPostCommitStateAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
            => store.RepairPostCommitStateAsync(request, cancellationToken);

        public Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default)
            => store.MarkSessionAbnormalAsync(monthKey, sessionId, endTime, cancellationToken);
    }

    private sealed class ProtocolDriver(InMemoryPlcDriver memory, Station station) : IPlcDriver
    {
        public InMemoryPlcDriver Memory => memory;
        public bool AcceptRequests { get; set; } = true;
        public long? ChangeBeforeWrite { get; set; }
        public int RequestWrites { get; private set; }
        public int AcceptedCount { get; private set; }
        public PlcBrand Brand => memory.Brand;
        public PlcCapabilities Capabilities => memory.Capabilities;
        public bool IsConnected => memory.IsConnected;
        public void SetEvent(long identity)
        {
            memory.SetWords(station.EventIdAddress, DeviceEventProtocol.Request(identity, 0)[..4]);
            memory.SetWord(station.TriggerAddress, 1);
        }
        public bool TryParseAddress(string text, out PlcAddress address) => memory.TryParseAddress(text, out address);
        public Task ConnectAsync(CancellationToken cancellationToken = default) => memory.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => memory.DisconnectAsync(cancellationToken);
        public Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
            => memory.ReadWordsAsync(start, wordCount, cancellationToken);
        public async Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
        {
            await memory.WriteWordsAsync(start, words, cancellationToken);
            if (start.Original != station.ConfirmationRequestAddress) return;
            RequestWrites++;
            if (ChangeBeforeWrite is { } changed) SetEvent(changed);
            if (!AcceptRequests || !words.Take(4).SequenceEqual(Enumerable.Range(0, 4).Select(i => memory.GetWord($"D{9000 + i}")))) return;
            memory.SetWords(station.ConfirmationAckAddress, words);
            memory.SetWord(station.TriggerAddress, words[4]);
            AcceptedCount++;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
