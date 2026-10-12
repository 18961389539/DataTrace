using DataTrace.Application.Realtime;
using DataTrace.Domain.Constants;
using System.Diagnostics.Metrics;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;

namespace DataTrace.Collector;

/// <summary>
/// 采集侧的运行时观测登记处。只活在内存里：诊断页读它，采集循环写它。
/// </summary>
/// <remarks>
/// 为什么不是 <c>IRuntimeStatusHub</c> 的一部分：hub 的语义是"状态变了就广播"，
/// 而这里是高频流水。混进去会让看板被 PLC 请求刷屏重画。
/// </remarks>
public sealed class CollectorDiagnostics : ICollectorDiagnostics, IDisposable
{
    /// <summary>保留最近多少轮采集循环。取值来自 <see cref="SystemDefaults"/>，因为诊断页的说明会复述它。</summary>
    public const int LoopCapacity = SystemDefaults.CollectorLoopHistory;

    private readonly object _gate = new();
    private readonly Queue<CollectorLoopTick> _ticks = new(LoopCapacity);
    private readonly Dictionary<int, PlcTrafficView> _traffic = new();
    private const int LatencyStationCapacity = 128;
    private const int LatencySampleCapacity = 512;
    private const int ActiveCollectionCapacity = 128;
    private static readonly TimeSpan LatencyRetention = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, Queue<(DateTime At, double Ms)>> _latencies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DateTime StartedAt, long StartedTimestamp)> _activeCollections = new(StringComparer.Ordinal);
    private readonly int _collectionMaxAgeSeconds;
    private readonly MeterListener _meterListener;
    private IReadOnlyList<StationLatencyView> _latencySnapshot = [];
    private IReadOnlyList<ActiveCollectionView> _activeSnapshot = [];
    private IReadOnlyList<PlcTrafficView> _trafficSnapshot = [];
    private CollectorLoopView _loop = CollectorLoopView.Empty(LoopCapacity);

    public CollectorDiagnostics(IConfiguration? configuration = null)
    {
        _collectionMaxAgeSeconds = Math.Max(0, configuration?.GetValue<int?>("DataTrace:Diagnostics:CollectionMaxAgeSeconds") ?? 0);
        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == CollectMetrics.MeterName && instrument.Name == "dt.collect.e2e_duration")
                listener.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            string? station = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "station")
                {
                    station = tag.Value?.ToString();
                    break;
                }
            }
            if (!string.IsNullOrWhiteSpace(station) && double.IsFinite(value) && value >= 0)
                RecordLatency(station, value, DateTime.Now);
        });
        _meterListener.Start();
    }

    public IReadOnlyList<StationLatencyView> RecentStationLatencies
    {
        get
        {
            lock (_gate)
            {
                var count = _latencies.Sum(x => x.Value.Count);
                PruneLatencies(DateTime.Now);
                if (count != _latencies.Sum(x => x.Value.Count)) PublishLatencies();
                return _latencySnapshot;
            }
        }
    }

    public IReadOnlyList<ActiveCollectionView> ActiveCollections
    {
        get
        {
            lock (_gate)
            {
                PublishActiveCollections();
                return _activeSnapshot;
            }
        }
    }

    public int CollectionMaxAgeSeconds => _collectionMaxAgeSeconds;

    public IReadOnlyList<ActiveCollectionView> OverdueCollections
        => _collectionMaxAgeSeconds <= 0 ? [] : ActiveCollections.Where(x => x.ElapsedMs >= _collectionMaxAgeSeconds * 1000L).ToList();

    public void BeginCollection(string stationCode, DateTime startedAt)
    {
        lock (_gate)
        {
            if (!_activeCollections.ContainsKey(stationCode) && _activeCollections.Count >= ActiveCollectionCapacity) return;
            _activeCollections[stationCode] = (startedAt, Stopwatch.GetTimestamp());
            PublishActiveCollections();
        }
    }

    public void EndCollection(string stationCode)
    {
        lock (_gate)
        {
            if (_activeCollections.Remove(stationCode))
                PublishActiveCollections();
        }
    }

    private void PublishActiveCollections()
    {
        var now = Stopwatch.GetTimestamp();
        _activeSnapshot = _activeCollections.OrderBy(x => x.Value.StartedAt)
            .Select(x => new ActiveCollectionView(x.Key, x.Value.StartedAt,
                (long)Stopwatch.GetElapsedTime(x.Value.StartedTimestamp, now).TotalMilliseconds)).ToList();
    }

    private void RecordLatency(string station, double milliseconds, DateTime at)
    {
        lock (_gate)
        {
            PruneLatencies(at);
            if (!_latencies.TryGetValue(station, out var samples))
            {
                if (_latencies.Count >= LatencyStationCapacity)
                    _latencies.Remove(_latencies.MinBy(x => x.Value.LastOrDefault().At).Key);
                samples = new Queue<(DateTime, double)>();
                _latencies[station] = samples;
            }
            samples.Enqueue((at, milliseconds));
            while (samples.Count > LatencySampleCapacity) samples.Dequeue();
            PublishLatencies();
        }
    }

    private void PruneLatencies(DateTime now)
    {
        foreach (var key in _latencies.Keys.ToList())
        {
            var samples = _latencies[key];
            while (samples.Count > 0 && now - samples.Peek().At > LatencyRetention) samples.Dequeue();
            if (samples.Count == 0) _latencies.Remove(key);
        }
    }

    private void PublishLatencies()
    {
        _latencySnapshot = _latencies.Select(pair =>
        {
            var ordered = pair.Value.Select(x => x.Ms).Order().ToArray();
            return new StationLatencyView(pair.Key, ordered.Length, Percentile(ordered, .95), Percentile(ordered, .99), pair.Value.Last().At);
        }).OrderBy(x => x.StationCode, StringComparer.Ordinal).ToList();
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    public void Dispose() => _meterListener.Dispose();

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
