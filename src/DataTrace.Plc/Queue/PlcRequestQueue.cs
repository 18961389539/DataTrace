using System.Threading.Channels;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;

namespace DataTrace.Plc.Queue;

/// <summary>单连接串行请求队列，避免 IoTClient 并发串包。</summary>
public sealed class PlcRequestQueue : IAsyncDisposable
{
    private readonly IPlcDriver _driver;
    private readonly Channel<WorkItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private readonly bool _ownsDriver;

    public PlcRequestQueue(IPlcDriver driver, bool ownsDriver = false)
    {
        _driver = driver;
        _ownsDriver = ownsDriver;
        _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _loop = Task.Run(ProcessLoopAsync);
    }

    public IPlcDriver Driver => _driver;

    public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ushort[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(async ct =>
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await _driver.ReadWordsAsync(start, wordCount, ct).ConfigureAwait(false);
        }, tcs, cancellationToken);

        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ushort[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(async ct =>
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            await _driver.WriteWordsAsync(start, words, ct).ConfigureAwait(false);
            return [];
        }, tcs, cancellationToken);

        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_driver.IsConnected)
        {
            return;
        }

        Exception? last = null;
        for (var i = 0; i < 3; i++)
        {
            try
            {
                await _driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(_reconnectDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new PlcDriverException("PLC 连接失败", last ?? new InvalidOperationException());
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                if (item.Cancellation.IsCancellationRequested)
                {
                    item.Completion.TrySetCanceled(item.Cancellation);
                    continue;
                }

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, item.Cancellation);
                    var result = await item.Execute(linked.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException oce)
                {
                    item.Completion.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (_ownsDriver)
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }

    private sealed record WorkItem(
        Func<CancellationToken, Task<ushort[]>> Execute,
        TaskCompletionSource<ushort[]> Completion,
        CancellationToken Cancellation);
}
