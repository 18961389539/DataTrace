using System.Diagnostics.Metrics;

namespace DataTrace.Plc;

/// <summary>
/// PLC 请求队列的排队与 IO 耗时指标（性能基线 P0 的"PLC 排队 / 读取"两段）。
/// 队列是单连接串行收口：排队时间反映"这台 PLC 是否被别的轮次压住了"，
/// IO 时间反映"这台 PLC 本身的通讯快慢/重连成本"。两者分开才能定位瓶颈在哪一侧。
/// </summary>
public static class PlcMetrics
{
    public const string MeterName = "DataTrace.Plc";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>(
        "dt.plc.queue_wait",
        unit: "ms",
        description: "请求从入队到开始执行的等待时间（标签：op=read/write）");

    private static readonly Histogram<double> IoDuration = Meter.CreateHistogram<double>(
        "dt.plc.io_duration",
        unit: "ms",
        description: "单次 PLC 交互耗时，含按需重连（标签：op=read/write）");

    public const string OpRead = "read";
    public const string OpWrite = "write";

    public static void RecordQueueWait(double milliseconds, string op)
        => QueueWait.Record(milliseconds, new KeyValuePair<string, object?>("op", op));

    public static void RecordIo(double milliseconds, string op)
        => IoDuration.Record(milliseconds, new KeyValuePair<string, object?>("op", op));
}
