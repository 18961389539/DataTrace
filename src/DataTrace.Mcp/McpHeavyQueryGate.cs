using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace DataTrace.Mcp;

/// <summary>
/// 报表、趋势与过程能力查询共用的并发闸门：SQLite 写入与这些长查询争用同一数据盘，
/// 超过并发上限时短暂等待，等不到就明确拒绝，而不是把采集写延迟拖长。
/// </summary>
public sealed class McpHeavyQueryGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _waitTimeout;

    public McpHeavyQueryGate(IOptions<McpOptions> options)
    {
        var value = options.Value;
        _semaphore = new SemaphoreSlim(Math.Max(1, value.HeavyQueryConcurrency));
        _waitTimeout = TimeSpan.FromSeconds(Math.Max(1, value.RequestTimeoutSeconds));
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!await _semaphore.WaitAsync(_waitTimeout, cancellationToken).ConfigureAwait(false))
        {
            throw new McpException("查询繁忙，请稍后重试。");
        }
        return new Releaser(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                semaphore.Release();
            }
        }
    }
}
