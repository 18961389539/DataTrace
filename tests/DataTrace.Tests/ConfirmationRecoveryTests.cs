using DataTrace.Application.Identity;
using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Collector;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Plc;
using DataTrace.Plc.Queue;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace DataTrace.Tests;

public sealed class ConfirmationRecoveryTests
{
    [Theory]
    [InlineData(PendingConfirmationResolutionMode.SameEventConfirmed)]
    [InlineData(PendingConfirmationResolutionMode.DeviceAlreadyAcknowledged)]
    public async Task Verified_manual_resolution_keeps_audit_and_cannot_be_executed_twice(PendingConfirmationResolutionMode mode)
    {
        await using var h = await CollectHarness.CreateAsync();
        var (station, pending) = await PendingAsync(h);
        if (mode == PendingConfirmationResolutionMode.DeviceAlreadyAcknowledged)
            h.Simulator.SetInt16(station.TriggerAddress, ResultCodes.Success);
        var service = Service(h);
        var result = await service.ResolveAsync(pending.MonthKey, pending.Id, "engineer", "现场原件已核对", "设备和流水号核对记录", mode);
        Assert.True(result.Resolved, result.Error);
        Assert.Null(await h.Provider.GetRequiredService<IPendingWriteBackStore>().FindOldestUnconfirmedAsync(station.Id));
        var repeated = await service.ResolveAsync(pending.MonthKey, pending.Id, "engineer", "重复请求", "设备记录", mode);
        Assert.False(repeated.Resolved);
        Assert.Single((await h.QueryAsync(pending.PalletCode)).Items);
    }

    [Fact]
    public async Task Failed_device_readback_keeps_durable_guard()
    {
        await using var h = await CollectHarness.CreateAsync();
        var (_, pending) = await PendingAsync(h);
        h.Simulator.SetFailWrites(true);
        var result = await Service(h).ResolveAsync(pending.MonthKey, pending.Id, "engineer", "核对", "原设备记录", PendingConfirmationResolutionMode.SameEventConfirmed);
        Assert.False(result.Resolved);
        Assert.NotNull(await h.Provider.GetRequiredService<IPendingWriteBackStore>().FindOldestUnconfirmedAsync(pending.StationId));
    }

    [Fact]
    public async Task Changed_target_and_still_triggered_device_cannot_be_cleared_as_acknowledged()
    {
        await using var h = await CollectHarness.CreateAsync();
        var (station, pending) = await PendingAsync(h);
        var service = Service(h);
        var active = await service.ResolveAsync(pending.MonthKey, pending.Id, "engineer", "核对", "现场记录", PendingConfirmationResolutionMode.DeviceAlreadyAcknowledged);
        Assert.False(active.Resolved);
        h.Plc.Host = "changed.example";
        await h.ConfigRepository.SavePlcConnectionAsync(h.Plc);
        var changed = await service.ResolveAsync(pending.MonthKey, pending.Id, "engineer", "核对", "现场记录", PendingConfirmationResolutionMode.SameEventConfirmed);
        Assert.False(changed.Resolved);
        Assert.Equal(ResultCodes.Trigger, unchecked((short)h.Simulator.GetWord(station.TriggerAddress)));
    }

    [Fact]
    public async Task Device_already_acknowledged_requires_matching_persistent_event_ack()
    {
        await using var h = await CollectHarness.CreateAsync();
        var (station, pending) = await PendingAsync(h);
        station.EventIdAddress = "D300";
        station.ConfirmationRequestAddress = "D310";
        station.ConfirmationAckAddress = "D320";
        await h.ConfigRepository.SaveStationAsync(station);

        const long eventId = 42;
        var factory = h.Provider.GetRequiredService<RuntimeDbFactory>();
        await using (var db = factory.Open(pending.MonthKey))
        {
            var stored = await db.PendingWriteBacks.SingleAsync(x => x.Id == pending.Id);
            stored.DeviceEventId = eventId;
            stored.EventIdAddress = station.EventIdAddress;
            stored.ConfirmationRequestAddress = station.ConfirmationRequestAddress;
            stored.ConfirmationAckAddress = station.ConfirmationAckAddress;
            await db.SaveChangesAsync();
        }

        // Trigger is inactive, but the PLC's durable ACK is blank/stale. It must not settle the old record.
        h.Simulator.SetInt16(station.TriggerAddress, ResultCodes.Success);
        h.Simulator.SetWords(station.ConfirmationAckAddress, [0, 0, 0, 0, 0]);
        var result = await Service(h).ResolveAsync(pending.MonthKey, pending.Id, "engineer", "核对", "现场记录",
            PendingConfirmationResolutionMode.DeviceAlreadyAcknowledged);

        Assert.False(result.Resolved);
        Assert.Contains("回执", result.Error);
        Assert.Single(await h.Provider.GetRequiredService<IPendingWriteBackStore>().ListManualReviewAsync());
    }

    private static ConfirmationRecoveryService Service(CollectHarness h)
        => new(h.Provider.GetRequiredService<IPendingWriteBackStore>(), new AuthorizedOperator(), h.ConfigRepository,
            new Queues(h.Plc.Id, h.Queue), h.Provider.GetRequiredService<IDataMaintenanceBarrier>(), h.StatusHub);

    private static async Task<(Station, PendingWriteBack)> PendingAsync(CollectHarness h)
    {
        var station = h.Station(0);
        SimulatorScenario.LoadStationCycle(h.Simulator, h.Plc, station, "PRECV01", new SimulatedCycleOptions { Random = new Random(19) });
        h.Simulator.SetFailWrites(true);
        await h.RunAsync(station);
        h.Simulator.SetFailWrites(false);
        var pending = await h.Provider.GetRequiredService<IPendingWriteBackStore>().FindOldestUnconfirmedAsync(station.Id);
        Assert.NotNull(pending);
        await h.Pipeline.RetryPendingConfirmationAsync(station, h.Plc, h.Snapshot, h.Queue, pending, CancellationToken.None);
        return (station, pending);
    }

    private sealed class Queues(int id, PlcRequestQueue queue) : IPlcQueueAccess
    {
        public bool TryGetTrialQueue(int plcConnectionId, out PlcRequestQueue result, out string? reason)
        {
            result = queue;
            reason = plcConnectionId == id ? null : "Unavailable";
            return plcConnectionId == id;
        }
    }

    private sealed class AuthorizedOperator : IConfigurationAuthorizer
    {
        public Task AuthorizeChangeAsync(string actor, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AuthorizeRoleAsync(string actor, IReadOnlyCollection<string> allowedRoles, CancellationToken cancellationToken = default, bool allowMustChangePassword = false) => Task.CompletedTask;
        public Task AuthorizeCurrentRoleAsync(IReadOnlyCollection<string> allowedRoles, CancellationToken cancellationToken = default, bool allowMustChangePassword = false) => Task.CompletedTask;
    }
}
