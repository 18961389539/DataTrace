using DataTrace.Domain.Constants;

namespace DataTrace.Plc.Queue;

/// <summary>一次 PLC 请求是读还是写。</summary>
public enum PlcExchangeKind
{
    Read = 0,
    Write = 1
}

/// <summary>
/// 一次 PLC 请求的观测记录：发给谁、读/写多少、花了多久、成没成、值是多少。
/// </summary>
/// <remarks>
/// <paramref name="Values"/> 只留前几个字（见 <see cref="PlcTrafficLog.ValuesKept"/>）：
/// 排障要看的是"这次到底读回来什么"，完整数据仍在记录里，没必要把整块缓冲区抄一遍。
/// <paramref name="DurationMs"/> 是**驱动调用本身**的耗时，不含排队等待 ——
/// 排队时间由采集循环的整轮耗时体现，两处口径分开才能看出是"PLC 慢"还是"队列堵"。
/// </remarks>
public sealed record PlcExchange(
    DateTime At,
    PlcExchangeKind Kind,
    string Address,
    int WordCount,
    long DurationMs,
    bool Ok,
    string? Error,
    IReadOnlyList<ushort> Values);

/// <summary>一台 PLC 的通信观测快照。</summary>
/// <param name="LastSuccessAt">
/// 最近一次成功请求的时刻（进程启动以来；还没有成功过时为 null）。
/// "上一次成功是多久以前"是断线判读里最快的一条证据：失败连片时，光看失败数看不出已经断了十分钟还是十秒。
/// </param>
/// <param name="RecentFailures">
/// 另外独立保留的失败记录（见 <see cref="PlcTrafficLog.FailureCapacity"/>）。
/// 它和 <paramref name="Recent"/> 会重叠，重叠多少取决于失败有多密 ——
/// 断线时 Recent 里本来就全是失败，这一份没有额外信息；偶发故障时，它是唯一还留着证据的地方。
/// </param>
public sealed record PlcTrafficSnapshot(
    int TotalCount,
    int FailureCount,
    long LastDurationMs,
    long MaxDurationMs,
    DateTime? LastSuccessAt,
    IReadOnlyList<PlcExchange> Recent,
    IReadOnlyList<PlcExchange> RecentFailures)
{
    public static PlcTrafficSnapshot Empty { get; } = new(0, 0, 0, 0, null, [], []);
}

/// <summary>
/// 每台 PLC 一个的请求流水，只保留最近若干条。
/// </summary>
/// <remarks>
/// 现场排障要的是"刚才那几拍发生了什么"，不是完整审计 —— 后者有滚动日志。
/// 容量刻意做小（见 <see cref="Capacity"/>）并且**不提供开关**：
/// 一个需要配置项才能打开的观测手段，出事时多半是关着的；而这里的代价只有几十条记录。
/// 旧条目自然被挤掉，进程重启即清空，不占磁盘。
/// </remarks>
public sealed class PlcTrafficLog
{
    /// <summary>保留的最近请求条数。取值来自 <see cref="SystemDefaults"/>，因为诊断页的说明会复述它。</summary>
    public const int Capacity = SystemDefaults.PlcTrafficCapacity;

    /// <summary>
    /// 失败记录额外保留的条数。
    /// </summary>
    /// <remarks>
    /// 高频请求下 <see cref="Capacity"/> 条只覆盖几秒，偶发故障会被整段冲掉 ——
    /// 诊断页因此出现过"标题写着失败 12、展开后 40 条全是成功"的荒唐状态。
    /// </remarks>
    public const int FailureCapacity = SystemDefaults.PlcTrafficFailureCapacity;

    /// <summary>每条记录最多留存的值个数。</summary>
    public const int ValuesKept = 8;

    private readonly object _gate = new();
    private readonly Queue<PlcExchange> _entries = new(Capacity);
    private readonly Queue<PlcExchange> _failuresKept = new(FailureCapacity);
    private int _total;
    private int _failures;
    private long _lastDurationMs;
    private long _maxDurationMs;
    private DateTime? _lastSuccessAt;
    private PlcTrafficSnapshot _snapshot = PlcTrafficSnapshot.Empty;

    public void Record(PlcExchange exchange)
    {
        lock (_gate)
        {
            _total++;
            if (!exchange.Ok)
            {
                _failures++;

                // 失败另存一份：高频成功请求几秒就能把故障挤出 _entries 的窗口。
                _failuresKept.Enqueue(exchange);
                while (_failuresKept.Count > FailureCapacity)
                {
                    _failuresKept.Dequeue();
                }
            }
            else
            {
                // 用记录自己的时刻而不是 DateTime.Now：与流水里的时间戳同一口径，读的时候不用换算。
                _lastSuccessAt = exchange.At;
            }

            _lastDurationMs = exchange.DurationMs;
            if (exchange.DurationMs > _maxDurationMs)
            {
                _maxDurationMs = exchange.DurationMs;
            }

            _entries.Enqueue(exchange);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
            }

            // 快照在写入时就备好：诊断页每次刷新都要读，没必要每次都复制一遍队列。
            // 顺序按最新在前，页面直接照渲染。
            _snapshot = new PlcTrafficSnapshot(
                _total,
                _failures,
                _lastDurationMs,
                _maxDurationMs,
                _lastSuccessAt,
                _entries.Reverse().ToList(),
                _failuresKept.Reverse().ToList());
        }
    }

    public PlcTrafficSnapshot Snapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <summary>把返回值裁到 <see cref="ValuesKept"/> 个。传 null 表示一次写请求（没有读回的东西）。</summary>
    public static IReadOnlyList<ushort> Trim(ushort[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return [];
        }

        return values.Length <= ValuesKept
            ? values
            : values.AsSpan(0, ValuesKept).ToArray();
    }
}
