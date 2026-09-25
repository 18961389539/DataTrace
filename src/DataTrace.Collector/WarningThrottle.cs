using System.Collections.Concurrent;

namespace DataTrace.Collector;

/// <summary>
/// 按 key 限流的告警开关。
/// </summary>
/// <remarks>
/// 采集循环每几十毫秒跑一轮，配置类问题（地址没进读计划、地址解析不了）会一轮一条地刷屏，
/// 真正的故障反而被淹掉。这里同一个 key 最多每分钟放行一条，问题修好后再次出现也还能提醒。
/// </remarks>
internal sealed class WarningThrottle(TimeSpan? interval = null)
{
    private readonly ConcurrentDictionary<string, long> _lastMs = new(StringComparer.Ordinal);
    private readonly long _intervalMs = (long)(interval ?? TimeSpan.FromMinutes(1)).TotalMilliseconds;

    public bool ShouldWarn(string key)
    {
        var now = Environment.TickCount64;
        if (_lastMs.TryGetValue(key, out var last) && now - last < _intervalMs)
        {
            return false;
        }

        _lastMs[key] = now;
        return true;
    }
}