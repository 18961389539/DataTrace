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

/// <summary>
/// 不良 / 预警 Top N 的结果：截断后的榜单 + 区间内的全部次数。
/// <see cref="Total"/> 是占比的分母，必须是全部次数而不是榜内合计 ——
/// 拿榜内合计当分母会永远显示 100%，看不出"榜外还有一大截"。
/// </summary>
public sealed class IssueTopReport
{
    public IReadOnlyList<IssueTopItem> Items { get; init; } = [];

    /// <summary>区间内全部次数（含榜外的那些）。</summary>
    public int Total { get; init; }
}

public sealed class TrendPoint
{
    public DateTime Time { get; init; }
    public double Value { get; init; }
    public string PalletCode { get; init; } = "";

    /// <summary>采集当时落库的规格下限；两列都为 null 表示那批数据早于"限值随记录落库"。</summary>
    public double? LowerLimit { get; init; }

    /// <summary>采集当时落库的规格上限；过程能力靠它按"限值有没有动过"分段。</summary>
    public double? UpperLimit { get; init; }
}


/// <summary>按产品型号汇总的产量与直通率。未选型号（RecipeCode 为空）单独一行，显示名由 UI 处理。</summary>
public sealed class RecipeThroughput
{
    public string RecipeCode { get; init; } = "";
    public int Total { get; init; }
    public int Ok { get; init; }
    public int Ng { get; init; }
    public int Pending { get; init; }
    /// <summary>直通率 = Ok / (Ok+Ng)；分母为 0 时为 0。</summary>
    public double PassRate => Ok + Ng == 0 ? 0 : (double)Ok / (Ok + Ng);
}

/// <summary>
/// 产量报表的两个切面：按日与按型号。
/// 两者是同一次取数上的两种分组，合在一起返回，避免把区间内全部记录查两遍。
/// </summary>
public sealed class ThroughputReport
{
    public IReadOnlyList<DailyThroughput> ByDay { get; init; } = [];

    public IReadOnlyList<RecipeThroughput> ByRecipe { get; init; } = [];
}

public interface IReportService
{
    /// <summary>产量报表：按日与按型号两个切面，同一次取数完成。</summary>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<ThroughputReport> GetThroughputAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>超规格点位 Top N（真实不良）。</summary>
    /// <param name="stationId">工站过滤：null = 全部工站。</param>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<IssueTopReport> GetDefectTopAsync(DateTime from, DateTime to, int? stationId, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>预警点位 Top N（落在黄区，尚未判废）。用于在出不良之前发现漂移。</summary>
    /// <param name="stationId">工站过滤：null = 全部工站。</param>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<IssueTopReport> GetWarningTopAsync(DateTime from, DateTime to, int? stationId, int take = 10, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    /// <param name="take">最多取区间内<b>最新</b>的多少点；0 表示不限。</param>
    Task<IReadOnlyList<TrendPoint>> GetTrendAsync(DateTime from, DateTime to, int tagId, string? recipeCode = null, int take = 0, CancellationToken cancellationToken = default);
}
