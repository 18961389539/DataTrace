using DataTrace.Infrastructure.Storage;

namespace DataTrace.Tests;

public sealed class DataMaintenanceBarrierTests
{
    [Fact]
    public async Task Backup_waits_for_inflight_operation_and_blocks_later_writes()
    {
        var barrier = new DataMaintenanceBarrier();
        var first = await barrier.EnterReadAsync();
        var backup = barrier.EnterWriteAsync().AsTask();
        Assert.False(backup.IsCompleted);
        var later = barrier.EnterReadAsync().AsTask();
        Assert.False(later.IsCompleted);
        await first.DisposeAsync();
        var exclusive = await backup.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(later.IsCompleted);
        await exclusive.DisposeAsync();
        await using var resumed = await later.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Cancelled_backup_does_not_leave_the_barrier_locked()
    {
        var barrier = new DataMaintenanceBarrier();
        var active = await barrier.EnterReadAsync();
        using var cancellation = new CancellationTokenSource();
        var backup = barrier.EnterWriteAsync(cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backup);
        await active.DisposeAsync();
        await using var next = await barrier.EnterWriteAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }
}
