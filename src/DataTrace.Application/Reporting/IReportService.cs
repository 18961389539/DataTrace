using DataTrace.Domain.Enums;

namespace DataTrace.Application.Reporting;

public sealed class DailyThroughput
{
    /// <summary>这一班的开始时刻。</summary>
    public DateTime Day { get; init; }

    /// <summary>给表格看的班次名，例如 09-28 08:00–20:00。</summary>
    public string ShiftLabel { get; init; } = "";

    public int Total { get; init; }
    public int Ok { get; init; }
    public int Ng { get; init; }

    /// <summary>不合格里，采集已经完成、产品被判废的件数（结果码 11，或采集成功但仍判废）。</summary>
    public int QualityNg { get; init; }

    /// <summary>不合格里，这一件没有采成的件数（结果码 3–10）。收紧规格限解释不了它们。</summary>
    public int CollectNg { get; init; }

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

    /// <summary>区间内全部次数（含榜外的那些）。不良榜这里只计越过红线，不含没读到数。</summary>
    public int Total { get; init; }

    /// <summary>必填点位没有数值的次数。和越过红线分开，避免同一个点名两种原因。</summary>
    public IReadOnlyList<IssueTopItem> MissingItems { get; init; } = [];

    public int MissingTotal { get; init; }
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

/// <summary>走出末站的一件，以及把它判废的点。</summary>
public sealed class FinishedPieceObservation
{
    public string MonthKey { get; init; } = "";
    public long SessionId { get; init; }
    public DateTime EndTime { get; init; }
    public Judgement Judgement { get; init; }
    public string RecipeCode { get; init; } = "";
    public IReadOnlyList<PieceStationMark> Stations { get; init; } = [];
    public IReadOnlyList<PieceFaultPoint> Faults { get; init; } = [];
}

public readonly record struct PieceStationMark(
    int StationId,
    string StationCode,
    Judgement Judgement,
    short ResultCode = 0,
    int Sequence = 0);

/// <summary>一个不合格点相对规格限的方向。没有数和没带限值时不算偏高或偏低。</summary>
public enum PointSide
{
    Unspecified = 0,
    High = 1,
    Low = 2,
    Missing = 3
}

public readonly record struct PieceFaultPoint(int StationId, string StationCode, string Name, PointSide Side = PointSide.Unspecified);

/// <summary>本班（或所选区间）把合格率拉下去最多的那一个点。</summary>
public sealed class PieceYieldDrag
{
    public string StationCode { get; init; } = "";
    public string PointName { get; init; } = "";
    public int PieceCount { get; init; }
    public int HighCount { get; init; }
    public int LowCount { get; init; }

    public string Text => string.IsNullOrWhiteSpace(PointName) || PointName == "不合格"
        ? $"不合格多出在 {StationCode}，{PieceCount} 件"
        : $"不合格多出在 {StationCode} {PointName}，{PieceCount} 件";

    public string SideText => (HighCount, LowCount) switch
    {
        (0, 0) => "",
        (_, 0) => $"偏高 {HighCount} 件",
        (0, _) => $"偏低 {LowCount} 件",
        _ => $"偏高 {HighCount} 件 · 偏低 {LowCount} 件"
    };
}

/// <summary>不合格件里，按工站顺序第一次判废出现最多的那一站。</summary>
public sealed class PieceFirstNg
{
    public string StationCode { get; init; } = "";
    public int PieceCount { get; init; }

    public string Text => $"最先坏在 {StationCode}，{PieceCount} 件";
}

/// <summary>直通率那个点上，还合格的读数离红线还有多远。</summary>
public sealed class SpecClearance
{
    public double Nearest { get; init; }
    public int NearCount { get; init; }
    public bool HasBand { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>按走出末站的一件汇总。任一站不合格，整件算不合格。</summary>
public sealed class PieceYieldReport
{
    public IReadOnlyList<DailyThroughput> ByShift { get; init; } = [];
    public IReadOnlyList<RecipeThroughput> ByRecipe { get; init; } = [];
    public PieceYieldDrag? Drag { get; init; }
    public PieceFirstNg? FirstNg { get; init; }
    public SpecClearance? Clearance { get; init; }
}

public interface IReportService
{
    /// <summary>产量报表：按日与按型号两个切面，同一次取数完成。</summary>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<ThroughputReport> GetThroughputAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 合格率按走出末站的一件算。末站完成时刻落在区间里才计入；
    /// 不选工站时，任一站不合格整件算不合格。选了工站时，只看这一站自己的判定。
    /// </summary>
    Task<PieceYieldReport> GetPieceYieldAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 某一班少做的件停在哪一站、停了多久。按各站完成时刻推算，不用人工确认。
    /// 工站和型号筛选不参与：停的是整条线，不是某一张报表的筛选结果。
    /// </summary>
    Task<ShiftStopReport> GetShiftStopsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

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
