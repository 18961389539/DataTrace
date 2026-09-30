using System.Diagnostics;
using System.Threading.Channels;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;

namespace DataTrace.Plc.Queue;

/// <summary>单连接串行请求队列，避免 IoTClient 并发串包。</summary>
/// <remarks>
/// 断线时这里会熔断：连续失败后进入一段冷却期，期间不再尝试重连而是立刻失败，
/// 并给上层一个 <see cref="IsCoolingDown"/> 用来跳过本轮扫描。
/// 否则每次请求都要走满"重连 3 次 × 2 秒"，采集循环会被一台掉线的 PLC 拖到几秒一轮。
/// </remarks>
public sealed class PlcRequestQueue : IAsyncDisposable
{
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromSeconds(60);

    private readonly IPlcDriver _driver;
    private readonly Channel<WorkItem> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly TimeSpan _reconnectDelay = TimeSpan.FromSeconds(2);
    private readonly bool _ownsDriver;
    private readonly Func<long> _monotonicMs;
    private int _failureStreak;
    private long _coolingUntilMs;

    /// <param name="monotonicMs">
    /// 单调毫秒时钟，默认取 <see cref="Environment.TickCount64"/>。
    /// </param>
    /// <remarks>
    /// 抽成可注入的时钟是为了让"冷却期是否还在"能被确定性地断言：
    /// 冷却窗口只有几秒，而断言要等本用例的续体被线程池调度起来才执行 ——
    /// 整机满载时这段调度延迟可以超过窗口，于是用例在负载高时偶发失败，
    /// 看起来像熔断坏了，其实只是断言跑得太晚。
    /// </remarks>
    public PlcRequestQueue(IPlcDriver driver, bool ownsDriver = false, Func<long>? monotonicMs = null)
    {
        _driver = driver;
        _ownsDriver = ownsDriver;
        _monotonicMs = monotonicMs ?? (static () => Environment.TickCount64);
        _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _loop = Task.Run(ProcessLoopAsync);
    }

    public IPlcDriver Driver => _driver;

    /// <summary>是否处于断线冷却期：此时请求会立刻失败，上层可据此跳过本轮扫描。</summary>
    public bool IsCoolingDown => _monotonicMs() < Interlocked.Read(ref _coolingUntilMs);

    /// <summary>
    /// 本连接的请求流水。队列是所有 PLC 请求的唯一收口，所以挂在这里就能看全 ——
    /// 触发扫描、工站点位读取、响应码回写、心跳写入，一条不漏。
    /// </summary>
    public PlcTrafficLog Traffic { get; } = new();

    public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ushort[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(async ct =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                var words = await _driver.ReadWordsAsync(start, wordCount, ct).ConfigureAwait(false);
                Traffic.Record(new PlcExchange(
                    DateTime.Now, PlcExchangeKind.Read, Describe(start), wordCount,
                    sw.ElapsedMilliseconds, true, null, PlcTrafficLog.Trim(words)));
                return words;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Traffic.Record(new PlcExchange(
                    DateTime.Now, PlcExchangeKind.Read, Describe(start), wordCount,
                    sw.ElapsedMilliseconds, false, ex.Message, []));
                throw;
            }
        }, tcs, cancellationToken);

        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<ushort[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(async ct =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                await _driver.WriteWordsAsync(start, words, ct).ConfigureAwait(false);
                // 写请求把"写下去的值"记进 Values：回写响应码出错时，第一个要确认的就是到底写了什么。
                Traffic.Record(new PlcExchange(
                    DateTime.Now, PlcExchangeKind.Write, Describe(start), words.Length,
                    sw.ElapsedMilliseconds, true, null, PlcTrafficLog.Trim(words)));
                return [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Traffic.Record(new PlcExchange(
                    DateTime.Now, PlcExchangeKind.Write, Describe(start), words.Length,
                    sw.ElapsedMilliseconds, false, ex.Message, PlcTrafficLog.Trim(words)));
                throw;
            }
        }, tcs, cancellationToken);

        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>给人看的地址文本：优先用配置里写的原样，其次按"区名+偏移"拼。</summary>
    private static string Describe(PlcAddress address)
        => string.IsNullOrWhiteSpace(address.Original) ? $"{address.Area}{address.Offset}" : address.Original;

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_driver.IsConnected)
        {
            return;
        }

        if (IsCoolingDown)
        {
            // 冷却期内立刻失败：重连的代价（超时 + 退避延迟）不该由每一轮扫描承担。
            throw new PlcDriverException($"PLC 处于重连冷却期（{RemainingCooldownSeconds()} 秒后重试）");
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

        OpenCooldown();
        throw new PlcDriverException("PLC 连接失败", last ?? new InvalidOperationException());
    }

    /// <summary>失败越连续，冷却越久（5s→10s→20s→40s→60s 封顶），避免无限重连打满网络与日志。</summary>
    private void OpenCooldown()
    {
        var streak = Interlocked.Increment(ref _failureStreak);
        var seconds = Math.Min(MaxCooldown.TotalSeconds, InitialCooldown.TotalSeconds * Math.Pow(2, Math.Min(streak - 1, 4)));
        Interlocked.Exchange(ref _coolingUntilMs, _monotonicMs() + (long)TimeSpan.FromSeconds(seconds).TotalMilliseconds);
    }

    private void ResetCooldown()
    {
        Interlocked.Exchange(ref _failureStreak, 0);
        Interlocked.Exchange(ref _coolingUntilMs, 0);
    }

    private int RemainingCooldownSeconds()
        => Math.Max(1, (int)Math.Ceiling((Interlocked.Read(ref _coolingUntilMs) - _monotonicMs()) / 1000.0));

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
                    if (_driver.IsConnected)
                    {
                        ResetCooldown();
                    }

                    item.Completion.TrySetResult(result);
                }
                catch (OperationCanceledException oce)
                {
                    item.Completion.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    // 只在"连接确实断了"时熔断：地址写错之类的业务错误不该牵连整条链路，
                    // 否则一个配错的点位会让整台 PLC 停采几十秒。已在冷却期里的失败不重复加长冷却。
                    if (!_driver.IsConnected && !IsCoolingDown)
                    {
                        OpenCooldown();
                    }

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
