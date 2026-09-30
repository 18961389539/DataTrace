using DataTrace.Application.Realtime;
using DataTrace.Domain.Constants;

namespace DataTrace.Collector;

/// <summary>
/// 采集侧的运行时观测登记处。只活在内存里：诊断页读它，采集循环写它。
/// </summary>
/// <remarks>
/// 为什么不是 <c>IRuntimeStatusHub</c> 的一部分：hub 的语义是"状态变了就广播"，
/// 而这里是高频流水。混进去会让看板被 PLC 请求刷屏重画。
/// </remarks>
public sealed class CollectorDiagnostics : ICollectorDiagnostics
{
    /// <summary>保留最近多少轮采集循环。取值来自 <see cref="SystemDefaults"/>，因为诊断页的说明会复述它。</summary>
    public const int LoopCapacity = SystemDefaults.CollectorLoopHistory;

    private readonly object _gate = new();
    private readonly Queue<CollectorLoopTick> _ticks = new(LoopCapacity);
    private readonly Dictionary<int, PlcTrafficView> _traffic = new();
    private IReadOnlyList<PlcTrafficView> _trafficSnapshot = [];
    private CollectorLoopView _loop = CollectorLoopView.Empty(LoopCapacity);

    public IReadOnlyList<PlcTrafficView> PlcTraffic
    {
        get
        {
            lock (_gate)
            {
                return _trafficSnapshot;
            }
        }
    }

    public CollectorLoopView Loop
    {
        get
        {
            lock (_gate)
            {
                return _loop;
            }
        }
    }

    public void PublishPlcTraffic(PlcTrafficView traffic)
    {
        lock (_gate)
        {
            _traffic[traffic.PlcConnectionId] = traffic;
            _trafficSnapshot = _traffic.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
        }
    }

    public void ForgetPlc(int plcConnectionId)
    {
        lock (_gate)
        {
            if (_traffic.Remove(plcConnectionId))
            {
                _trafficSnapshot = _traffic.Values.OrderBy(x => x.Name, StringComparer.Ordinal).ToList();
            }
        }
    }

    public void PublishLoopTick(CollectorLoopTick tick)
    {
        lock (_gate)
        {
            _ticks.Enqueue(tick);
            while (_ticks.Count > LoopCapacity)
            {
                _ticks.Dequeue();
            }

            var recent = _ticks.Reverse().ToList();
            _loop = new CollectorLoopView(
                LoopCapacity,
                recent,
                recent.Count == 0 ? 0 : (long)recent.Average(t => t.WorkMs),
                recent.Count == 0 ? 0 : recent.Max(t => t.WorkMs),
                recent.Count(t => t.Overran));
        }
    }
}
