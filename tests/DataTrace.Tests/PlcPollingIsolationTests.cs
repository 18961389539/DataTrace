using DataTrace.Application.Configuration;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Queue;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DataTrace.Tests;

public sealed class PlcPollingIsolationTests
{
    [Fact]
    public async Task Queue_shutdown_releases_requests_waiting_for_capacity()
    {
        var drivers = new IsolationDrivers(1);
        var queue = new PlcRequestQueue(drivers.Create(new PlcConnection { Id = 1 }));
        Assert.True(queue.Driver.TryParseAddress("D100", out var address));
        var calls = Enumerable.Range(0, 300).Select(_ => queue.ReadWordsAsync(address, 1)).ToArray();
        await drivers.SlowEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        foreach (var call in calls)
        {
            var failure = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.True(failure is OperationCanceledException or ChannelClosedException,
                $"Queued callers must finish on shutdown, including callers waiting for capacity: {failure}");
        }
    }

    [Fact]
    public async Task Slow_plc_does_not_delay_repeated_scans_of_a_healthy_plc()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var fastConnection = new PlcConnection
        {
            Name = "Independent healthy PLC", Brand = PlcBrand.Simulator, Enabled = true,
            Host = "127.0.0.1", Port = 5001, TimeoutMs = 1000
        };
        await harness.ConfigRepository.SavePlcConnectionAsync(fastConnection);
        using (var scope = harness.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ConfigDbContext>();
            var station = await db.Stations.SingleAsync(x => x.Id == harness.Station(1).Id);
            station.PlcConnectionId = fastConnection.Id;
            await db.SaveChangesAsync();
        }
        await harness.ConfigRepository.BumpVersionAsync();
        var drivers = new IsolationDrivers(harness.Plc.Id);
        var service = new CollectionHostedService(
            harness.Provider.GetRequiredService<IServiceScopeFactory>(), drivers,
            harness.Pipeline, harness.Provider.GetRequiredService<IPendingWriteBackStore>(),
            harness.Provider.GetRequiredService<ISpoolStore>(),
            harness.StatusHub, harness.Provider.GetRequiredService<ICollectorDiagnostics>(),
            harness.Provider.GetRequiredService<ILogger<CollectionHostedService>>());

        await ((IHostedService)service).StartAsync(CancellationToken.None);
        try
        {
            await drivers.SlowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var limit = DateTime.UtcNow.AddSeconds(3);
            while (Volatile.Read(ref drivers.HealthyReads) < 3 && DateTime.UtcNow < limit)
                await Task.Delay(20);

            Assert.True(Volatile.Read(ref drivers.HealthyReads) >= 3,
                "The healthy connection must keep polling while the other PLC's first read is still pending.");
            Assert.Equal(1, Volatile.Read(ref drivers.SlowReads));
        }
        finally
        {
            drivers.ReleaseSlow.TrySetResult();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ((IHostedService)service).StopAsync(stop.Token);
            service.Dispose();
        }
    }

    private sealed class IsolationDrivers(int slowId) : IPlcDriverFactory
    {
        public readonly TaskCompletionSource SlowEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseSlow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HealthyReads;
        public int SlowReads;
        public IPlcDriver Create(PlcConnection connection) => new Driver(this, connection.Id == slowId);

        private sealed class Driver(IsolationDrivers owner, bool slow) : IPlcDriver
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
            public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
            {
                if (slow)
                {
                    Interlocked.Increment(ref owner.SlowReads);
                    owner.SlowEntered.TrySetResult();
                    await owner.ReleaseSlow.Task.WaitAsync(cancellationToken);
                }
                else Interlocked.Increment(ref owner.HealthyReads);
                return new ushort[wordCount];
            }
            public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
                => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
