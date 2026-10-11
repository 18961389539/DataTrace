using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DataTrace.Collector;

/// <summary>
/// 采集链路的阶段耗时与积压指标（性能基线 P0）。
/// </summary>
/// <remarks>
/// 口径约定（与性能审查结论一致）：
/// - 标签只用有界维度：工站编码、PLC 连接 Id、阶段名。流水号等高基数值只进日志（见
///   StationCollectPipeline 的锚点日志），避免指标序列随件数无限增长。
/// - Histogram 由监控端（dotnet-counters / Prometheus / OTLP）聚合出 P95/P99，
///   本类不自己算分位数。
/// - 阶段切分：plc_read（块读，含排队）、frozen_lookup（在制型号快照解析）、
///   file_source（设备覆写文件读取）、evaluate（判定算法）、persist（曲线文件+月库事务）、
///   writeback（响应码单次写入+读回核对）。e2e 是触发到回写核对完成的总时长，
///   与锚点日志的 {DurationMs} 同源。
/// </remarks>
public static class CollectMetrics
{
    public const string MeterName = "DataTrace.Collector";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>(
        "dt.collect.stage_duration",
        unit: "ms",
        description: "采集链路各阶段耗时（标签：stage/station/plc）");

    private static readonly Histogram<double> E2eDuration = Meter.CreateHistogram<double>(
        "dt.collect.e2e_duration",
        unit: "ms",
        description: "触发到响应码写回核对的端到端耗时（标签：station）");

    private static readonly Histogram<double> PollRoundDuration = Meter.CreateHistogram<double>(
        "dt.poll.round_duration",
        unit: "ms",
        description: "轮询一轮全部工站触发检测的耗时");

    private static readonly Counter<long> CollectCompleted = Meter.CreateCounter<long>(
        "dt.collect.completed",
        description: "采集完成件数（标签：station/result 结果码）");

    public const string StagePlcRead = "plc_read";
    public const string StageFrozenLookup = "frozen_lookup";
    public const string StageFileSource = "file_source";
    public const string StageEvaluate = "evaluate";
    public const string StagePersist = "persist";
    public const string StageWriteBack = "writeback";

    public static void RecordStage(string stage, Stopwatch stopwatch, string station, string? plc = null)
        => RecordStage(stage, stopwatch.Elapsed.TotalMilliseconds, station, plc);

    public static void RecordStage(string stage, double milliseconds, string station, string? plc = null)
    {
        var tags = new TagList
        {
            { "stage", stage },
            { "station", station }
        };
        if (plc is not null)
        {
            tags.Add("plc", plc);
        }

        StageDuration.Record(milliseconds, tags);
    }

    public static void RecordE2E(double milliseconds, string station, short resultCode)
    {
        E2eDuration.Record(milliseconds, new KeyValuePair<string, object?>("station", station));
        CollectCompleted.Add(1, new KeyValuePair<string, object?>("station", station),
            new KeyValuePair<string, object?>("result", resultCode.ToString()));
    }

    public static void RecordPollRound(double milliseconds) => PollRoundDuration.Record(milliseconds);
}
