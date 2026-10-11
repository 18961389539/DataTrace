using System.Diagnostics.Metrics;
using DataTrace.Application.Runtime;

namespace DataTrace.Infrastructure.Persistence;

/// <summary>
/// 月库写入链路的两个高成本阶段（性能基线 P0 的"文件写入 / 数据库提交"）：
/// 曲线文件写盘（含 CRC 计算）与月库事务（多次 SaveChanges + Commit）。
/// </summary>
public static class PersistenceMetrics
{
    public const string MeterName = "DataTrace.Persistence";

    public static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Histogram<double> CurveWrite = Meter.CreateHistogram<double>(
        "dt.persist.curve_write",
        unit: "ms",
        description: "一次采集的曲线文件写盘总耗时（全部曲线）");

    private static readonly Histogram<double> DbCommit = Meter.CreateHistogram<double>(
        "dt.persist.db_commit",
        unit: "ms",
        description: "月库事务从 BEGIN 到 COMMIT 的耗时");

    public static void RecordCurveWrite(double milliseconds, int curveCount)
        => CurveWrite.Record(milliseconds, new KeyValuePair<string, object?>("curves", Math.Min(curveCount, 8).ToString()));

    public static void RecordDbCommit(double milliseconds) => DbCommit.Record(milliseconds);

    /// <summary>注册补传积压 Gauge：件数 + 最老一条滞留秒数（没有积压时报 0 秒）。</summary>
    public static void RegisterSpoolBacklogGauges(Func<SpoolBacklog> observe)
    {
        Meter.CreateObservableGauge(
            "dt.spool.backlog_files",
            () => new Measurement<double>(observe().Count),
            description: "补传队列当前文件数");
        Meter.CreateObservableGauge(
            "dt.spool.oldest_seconds",
            () =>
            {
                var oldest = observe().OldestAt;
                return new Measurement<double>(oldest is { } at ? Math.Max(0, (DateTime.Now - at).TotalSeconds) : 0);
            },
            description: "补传队列最老一条的滞留秒数（队列为空时为 0）");
    }
}
