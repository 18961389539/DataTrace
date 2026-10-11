using DataTrace.Application.Runtime;

namespace DataTrace.Infrastructure.Storage;

/// <summary>A writer-preferring async reader/writer barrier for snapshots and maintenance.</summary>
public sealed class DataMaintenanceBarrier : IDataMaintenanceBarrier
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _resource = new(1, 1);
    private readonly SemaphoreSlim _readerMutex = new(1, 1);
    private int _readers;

    public async ValueTask<IAsyncDisposable> EnterReadAsync(CancellationToken cancellationToken = default)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        _turnstile.Release();

        await _readerMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (++_readers == 1)
            {
                await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _readers--;
            throw;
        }
        finally
        {
            _readerMutex.Release();
        }

        return new Lease(this, read: true);
    }

    public async ValueTask<IAsyncDisposable> EnterWriteAsync(CancellationToken cancellationToken = default)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _resource.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, read: false);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
    }

    private async ValueTask ExitReadAsync()
    {
        await _readerMutex.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--_readers == 0)
            {
                _resource.Release();
            }
        }
        finally
        {
            _readerMutex.Release();
        }
    }

    private void ExitWrite()
    {
        _resource.Release();
        _turnstile.Release();
    }

    private sealed class Lease(DataMaintenanceBarrier owner, bool read) : IAsyncDisposable
    {
        private DataMaintenanceBarrier? _owner = owner;

        public async ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }

            if (read)
            {
                await current.ExitReadAsync().ConfigureAwait(false);
            }
            else
            {
                current.ExitWrite();
            }
        }
    }
}
