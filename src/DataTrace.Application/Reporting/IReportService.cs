namespace DataTrace.Application.Reporting;

public sealed class DailyThroughput
{
    public DateTime Day { get; init; }
    public int Total { get; init; }
    public int Ok { get; init; }
    public int Ng { get; init; }
    /// <summary>尚未产生 OK/NG 的记录数（Judgement.None）。</summary>
    public int None { get; init; }
    /// <summary>直通率 = OK / (OK+NG)，未判定不计入分母。</summary>
    public double FirstPassYield
    {
        get
        {
            var judged = Ok + Ng;
            return judged == 0 ? 0 : (double)Ok / judged;
        }
    }
}

/// <summary>不良 / 预警 Top N 的统计项。预警是黄区提示，不计入不良。</summary>
public sealed class IssueTopItem
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
}

public sealed class TrendPoint
{
    public DateTime Time { get; init; }
    public double Value { get; init; }
    public string PalletCode { get; init; } = "";
}

public interface IReportService
{
    Task<IReadOnlyList<DailyThroughput>> GetThroughputAsync(DateTime from, DateTime to, int? stationId, CancellationToken cancellationToken = default);

    /// <summary>超规格点位 Top N（真实不良）。</summary>
    Task<IReadOnlyList<IssueTopItem>> GetDefectTopAsync(DateTime from, DateTime to, int take = 10, CancellationToken cancellationToken = default);

    /// <summary>预警点位 Top N（落在黄区，尚未判废）。用于在出不良之前发现漂移。</summary>
    Task<IReadOnlyList<IssueTopItem>> GetWarningTopAsync(DateTime from, DateTime to, int take = 10, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TrendPoint>> GetTrendAsync(DateTime from, DateTime to, int tagId, CancellationToken cancellationToken = default);
}
