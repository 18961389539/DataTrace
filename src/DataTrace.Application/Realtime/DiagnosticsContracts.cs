namespace DataTrace.Application.Realtime;

/// <summary>一次 PLC 请求的展示视图。</summary>
/// <param name="IsWrite">true = 写（回写响应码、写心跳），false = 读。</param>
/// <param name="Values">读回或写下的前几个字。写请求也带值 —— 回写出错时第一个要确认的就是写了什么。</param>
public sealed record PlcExchangeView(
    DateTime At,
    bool IsWrite,
    string Address,
    int WordCount,
    long DurationMs,
    bool Ok,
    string? Error,
    IReadOnlyList<ushort> Values);

/// <summary>一台 PLC 的通信视图：累计计数 + 最近若干次请求。</summary>
/// <param name="LastSuccessAt">
/// 最近一次成功请求的时刻；有请求却一直是 null，说明从来没成功过。
/// 断线时"上次成功是多久以前"比失败数更快说明问题：失败数是累计值，看不出已经断了多久。
/// </param>
public sealed record PlcTrafficView(
    int PlcConnectionId,
    string Name,
    int TotalCount,
    int FailureCount,
    long LastDurationMs,
    long MaxDurationMs,
    DateTime? LastSuccessAt,
    IReadOnlyList<PlcExchangeView> Recent,
    IReadOnlyList<PlcExchangeView> RecentFailures)
{
    public static PlcTrafficView Empty(int plcConnectionId, string name)
        => new(plcConnectionId, name, 0, 0, 0, 0, null, [], []);
}

/// <summary>一轮采集扫描的耗时构成。不含随后的等待间隔。</summary>
public sealed record CollectorLoopTick(
    DateTime At,
    int ConfiguredIntervalMs,
    long WorkMs,
    long ScanMs,
    long HeartbeatMs,
    int PlcCount,
    int TriggeredCount,
    int CoolingDownPlcCount,
    int SkippedStationCount)
{
    /// <summary>
    /// 这一轮干活的时间已经超过配置的扫描间隔。
    /// </summary>
    /// <remarks>
    /// 节拍掉下来时最先看它：超了说明扫描不再是"每 IntervalMs 一轮"，
    /// 而工站的触发被识别得越来越晚 —— 现场看到的是"节拍莫名变慢"，与 PLC 断没断线无关。
    /// </remarks>
    public bool Overran => WorkMs > ConfiguredIntervalMs;
}

/// <summary>采集循环的近期表现。</summary>
public sealed record CollectorLoopView(
    int Capacity,
    IReadOnlyList<CollectorLoopTick> Ticks,
    long AverageWorkMs,
    long MaxWorkMs,
    int OverrunCount)
{
    public static CollectorLoopView Empty(int capacity) => new(capacity, [], 0, 0, 0);
}

/// <summary>
/// 诊断页要的运行时观测数据。
/// </summary>
/// <remarks>
/// 刻意**不做变更推送**（没有 <c>Changed</c> 事件）：PLC 请求每分钟成百上千次，
/// 每来一条就广播一次会让看板每 75 毫秒重画一遍。诊断页按自己的节拍拉取即可 ——
/// 观测手段本身不该成为被观测的负担。
/// </remarks>
public interface ICollectorDiagnostics
{
    IReadOnlyList<PlcTrafficView> PlcTraffic { get; }

    CollectorLoopView Loop { get; }

    /// <summary>登记一台 PLC 的流水快照。采集侧每轮调用一次。</summary>
    void PublishPlcTraffic(PlcTrafficView traffic);

    /// <summary>登记一轮采集循环。采集侧每轮调用一次。</summary>
    void PublishLoopTick(CollectorLoopTick tick);

    /// <summary>连接被移除时把它的流水分掉，免得诊断页一直显示一台已经不存在的 PLC。</summary>
    void ForgetPlc(int plcConnectionId);
}
