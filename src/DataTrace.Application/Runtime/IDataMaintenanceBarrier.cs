namespace DataTrace.Application.Runtime;

/// <summary>
/// Coordinates data-changing operations with full-data snapshots and retention.
/// Callers acquire one lease at the outer business-operation boundary; implementations
/// must not recursively acquire leases from stores called under that boundary.
/// </summary>
public interface IDataMaintenanceBarrier
{
    ValueTask<IAsyncDisposable> EnterReadAsync(CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> EnterWriteAsync(CancellationToken cancellationToken = default);
}
