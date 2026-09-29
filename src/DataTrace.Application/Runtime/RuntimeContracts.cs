using DataTrace.Application.Alarms;
using DataTrace.Application.Reporting;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Runtime;

public sealed class CollectSaveRequest
{
    public required string MonthKey { get; init; }
    public PalletSession? UpsertSession { get; init; }
    public bool CloseSession { get; init; }
    public required CollectRecord Record { get; init; }
    public IReadOnlyList<CurvePayloadWrite> Curves { get; init; } = [];
    public MesOutboxItem? MesOutbox { get; init; }
    public ActiveSessionIndex? ActiveSession { get; init; }
    public bool RemoveActiveSession { get; init; }
}

public sealed class CurvePayloadWrite
{
    public required CurveRecord Record { get; init; }
    public required CurvePayload Payload { get; init; }

    /// <summary>采集时就地算好的波形特征，随曲线记录一起落库。</summary>
    public IReadOnlyList<CurveFeature> Features { get; init; } = [];
}

public sealed class CurvePayload
{
    public required int PointCount { get; init; }
    public required IReadOnlyList<CurveSeriesPayload> Series { get; init; }
}

public sealed class CurveSeriesPayload
{
    public required string Name { get; init; }
    public required SeriesRole Role { get; init; }
    public required float[] Values { get; init; }
}

public sealed class CollectQueryRequest
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? PalletCode { get; init; }
    public string? SerialNo { get; init; }
    public int? StationId { get; init; }
    public Judgement? Judgement { get; init; }

    /// <summary>
    /// 结果码过滤，与「判定」是两件事：判定说的是超没超限，结果码说的是这次采集成没成。
    /// 排查「PLC 读取失败」这类问题时只有它能筛出来。
    /// </summary>
    public short? ResultCode { get; init; }

    /// <summary>
    /// 型号过滤：null = 全部；"" = 仅「未选型号」；其它 = 精确匹配 RecipeCode。
    /// </summary>
    public string? RecipeCode { get; init; }
    public int Skip { get; init; }
    public int Take { get; init; } = 50;

    /// <summary>排序列，默认触发时间（页面的默认视图）。</summary>
    public CollectSortField SortBy { get; init; } = CollectSortField.TriggerTime;

    /// <summary>是否降序。时间列默认 true（最近在前）；点击其余列表头时默认升序。</summary>
    public bool SortDescending { get; init; } = true;
}

/// <summary>
/// 记录列表可排序的列。排序必须在存储侧完成：分页跨月份库，只排当前页等于没排。
/// </summary>
public enum CollectSortField
{
    TriggerTime,
    SerialNo,
    PalletCode,
    StationCode,
    RecipeCode,
    Judgement,
    ResultCode,
    DurationMs
}

public sealed class CollectRecordListItem
{
    public required string MonthKey { get; init; }
    public required CollectRecord Record { get; init; }
}

/// <summary>
/// 单件履历：记录从对应月份库按精确流水号查出，并按会话 ID 归组。
/// Session 为空表示历史记录缺少有效的会话关联，不能假定履历完整。
/// </summary>
public sealed class CollectSessionTrace
{
    public required string MonthKey { get; init; }
    public long? SessionId { get; init; }
    public PalletSession? Session { get; init; }
    public required IReadOnlyList<CollectRecord> Records { get; init; }
}

public sealed class CollectQueryResult
{
    public required int Total { get; init; }
    public required IReadOnlyList<CollectRecordListItem> Items { get; init; }
}

/// <summary>
/// 产量统计的一格：某天 × 某型号 × 某判定的记录数。
/// 报表最终只画十来个格子，所以计数在 SQL 侧完成 ——
/// 区间内有几万条记录时，把行拉进内存再分组是纯浪费（实测占页面进入耗时的一大半）。
/// </summary>
public sealed class JudgementCount
{
    /// <summary>触发时刻所在自然日（当天 00:00）。和 <see cref="Hour"/> 一起才能归进班次。</summary>
    public DateTime Day { get; init; }

    /// <summary>触发时刻的小时，0–23。班次按整点切，同一小时不会跨班。</summary>
    public int Hour { get; init; }
    /// <summary>判定时生效的型号编码；空字符串表示当时未选型号。</summary>
    public string RecipeCode { get; init; } = "";
    public Judgement Judgement { get; init; }
    public int Count { get; init; }
}

/// <summary>不良 / 预警统计的窄投影。没读到数和越过红线都可能标着超限，用 <see cref="Missing"/> 分开。</summary>
public sealed class TagIssuePoint
{
    public string TagName { get; init; } = "";

    /// <summary>必填点位没有数值。这种行不是工艺超差。</summary>
    public bool Missing { get; init; }
}

/// <summary>还在规格限内的一次读数，用来算离红线还有多远。</summary>
public readonly record struct InSpecReading(double Value, double? Lower, double? Upper);

/// <summary>
/// 趋势统计的窄投影：只要某个点位自身的数值、时间与托盘码。
/// </summary>
public sealed class TagTrendPoint
{
    public DateTime Time { get; init; }
    public double Value { get; init; }
    public string PalletCode { get; init; } = "";

    /// <summary>采集当时生效的规格限，与 <see cref="Value"/> 同源；改动前的历史行为 null。</summary>
    public double? LowerLimit { get; init; }

    public double? UpperLimit { get; init; }
}

/// <summary>
/// 波形特征的窄投影：一次取回建基线与打分所需的全部字段。
/// 服务端按曲线定义 + 序列过滤，不展开任何导航集合。
/// </summary>
public sealed class CurveFeaturePoint
{
    public long CurveRecordId { get; init; }

    public DateTime Time { get; init; }

    public string PalletCode { get; init; } = "";

    /// <summary>所属采集记录的实际判定；只有 OK 样本可以建立基线。</summary>
    public Judgement ActualJudgement { get; init; }

    public bool IsNg => ActualJudgement == Judgement.Ng;

    /// <summary>该曲线判定时生效的产品型号；用于避免切换型号后基线整体失配。</summary>
    public string RecipeCode { get; init; } = "";

    public string SeriesName { get; init; } = "";

    public SeriesRole Role { get; init; }

    public CurveFeature Feature { get; init; } = new();
}

/// <summary>区间内某序列某个型号的样本计数。</summary>
public sealed record CurveRecipeSampleCount(string RecipeCode, int Total, int Ng, int Unjudged = 0);

/// <summary>采集写入：落一条记录，或把在制会话标成异常。</summary>
public interface ICollectWriter
{
    Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default);
    Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default);
}

/// <summary>采集查询：明细、会话履历。不包含报表聚合。</summary>
public interface ICollectQuery
{
    Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default);
    Task<CollectRecord?> GetRecordAsync(string monthKey, long recordId, CancellationToken cancellationToken = default);
    Task<PalletSession?> GetSessionAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CollectRecord>> GetSessionRecordsAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CollectSessionTrace>> FindSessionTracesBySerialNoAsync(string serialNo, CancellationToken cancellationToken = default);
}

/// <summary>报表与基线用的窄投影。调用方看不到写入和删库。</summary>
public interface IRuntimeAnalytics
{
    Task<IReadOnlyList<CollectRecord>> QueryForReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>
    /// 产量统计：按 日 × 型号 × 判定 在服务端聚合好的计数。
    /// </summary>
    /// <param name="stationId">工站过滤：null = 全部工站。</param>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<IReadOnlyList<JudgementCount>> CountJudgementsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 末站已经关掉的件。时间按会话结束时刻，不按各站的采集时刻。
    /// 型号在取数时过滤；工站筛选留给汇总，因为选了工站之后看的是这一站自己的判定。
    /// </summary>
    Task<IReadOnlyList<FinishedPieceObservation>> ListFinishedPiecesAsync(DateTime from, DateTime to, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 各站完成时刻。用来判断某一班哪一站停过。
    /// 调用方要多取班次两端以外的记录，才能看见跨过班次边界的那一段停顿。
    /// </summary>
    Task<IReadOnlyList<StationPass>> ListStationPassesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>日期范围内出现过的型号编码（含空串）；跨月库去重。</summary>
    Task<IReadOnlyList<string>> ListRecipeCodesAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>不良点位的窄投影查询（服务端按 IsOutOfLimit 过滤）。</summary>
    /// <param name="stationId">工站过滤：null = 全部工站。</param>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<IReadOnlyList<TagIssuePoint>> QueryOutOfLimitTagsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 某个点在合格件上、且没有越出规格限的读数。直通率那个点用它看离红线还有多远。
    /// 不选工站时，件的判定是整件合格；选了工站时，只看这一站自己合格的记录。
    /// </summary>
    Task<IReadOnlyList<InSpecReading>> ListInSpecReadingsAsync(
        DateTime from,
        DateTime to,
        string stationCode,
        string tagName,
        int? stationId,
        string? recipeCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>预警点位的窄投影查询（服务端按 IsWarning 过滤）。</summary>
    /// <param name="stationId">工站过滤：null = 全部工站。</param>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    Task<IReadOnlyList<TagIssuePoint>> QueryWarningTagsAsync(DateTime from, DateTime to, int? stationId, string? recipeCode = null, CancellationToken cancellationToken = default);

    /// <summary>单点位趋势的窄投影查询（服务端按 TagId 过滤）。</summary>
    /// <param name="recipeCode">型号过滤：null = 不限；"" = 仅「未选型号」；其它 = 精确匹配。</param>
    /// <param name="take">最多取区间内<b>最新</b>的多少点（升序返回）；0 表示不限。</param>
    Task<IReadOnlyList<TagTrendPoint>> QueryTagTrendAsync(DateTime from, DateTime to, int tagId, string? recipeCode = null, int take = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// 波形特征的窄投影查询：取某条曲线某个序列在区间内<b>最新的 take 条</b>。
    /// 基线应当反映设备"最近"的正常状态，而不是整段历史的平均，
    /// 因此用 take 限制样本量而不是把区间内全部特征拉进内存。
    /// </summary>
    /// <param name="seriesName">序列名；null 表示该曲线的全部序列。</param>
    /// <param name="recipeCodes">
    /// 型号过滤（服务端）；null 或空集合表示不限。必须传进来而不是取回内存再筛：
    /// take 是"最新 N 条"，先截断再按型号过滤的话，另一种型号最近产量大一点
    /// 就会把本型号的样本整段挤出去，基线直接空掉。
    /// </param>
    Task<IReadOnlyList<CurveFeaturePoint>> QueryCurveFeaturesAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int take,
        IReadOnlyCollection<string>? recipeCodes = null,
        CancellationToken cancellationToken = default,
        Judgement? judgement = null);

    /// <summary>
    /// 区间内某条曲线某序列的样本数按型号分布（型号编码 → 条数）。
    /// 不受 take 限制，用于解释"型号不匹配"，也让"取到多少条样本"是真数而不是截断后的数。
    /// </summary>
    Task<IReadOnlyList<CurveRecipeSampleCount>> CountCurveFeaturesByRecipeAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken = default);

    /// <summary>当月每条记录的工站与判定，供进程启动时重算连续 NG。库不存在时返回空，不新建月份库。</summary>
    Task<IReadOnlyList<StationJudgementMark>> ListMonthJudgementsAsync(string monthKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 每个工站每个点位最近 perTag 条观测，供启动时重算预警连续件数和过程漂移。
    /// 只取 since 之后的记录。库不存在时返回空，不新建月份库。
    /// </summary>
    Task<IReadOnlyList<TagObservation>> ListRecentTagObservationsAsync(
        string monthKey,
        DateTime since,
        int perTag,
        CancellationToken cancellationToken = default);
}

/// <summary>按月保留：列出月份库并整月删除。</summary>
public interface IRuntimeRetention
{
    IReadOnlyList<string> ListMonthKeys();
    Task DeleteMonthAsync(string monthKey, CancellationToken cancellationToken = default);
}

/// <summary>运行库的全部能力。新代码按上面的窄接口依赖，这个组合留给仍要一次拿全的调用方。</summary>
public interface IRuntimeStore : ICollectWriter, ICollectQuery, IRuntimeAnalytics, IRuntimeRetention
{
}

public interface IActiveSessionStore
{
    Task<ActiveSessionIndex?> FindByPalletAsync(string palletCode, CancellationToken cancellationToken = default);
    Task UpsertAsync(ActiveSessionIndex session, CancellationToken cancellationToken = default);
    Task RemoveByPalletAsync(string palletCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActiveSessionIndex>> ListAsync(CancellationToken cancellationToken = default);
}

public interface ISerialNumberGenerator
{
    Task<string> NextAsync(DateTime date, CancellationToken cancellationToken = default);
}

public interface ICurveFileStore
{
    /// <summary>
    /// 写入一条曲线的采样负载，返回实际落盘的相对路径与校验信息。
    /// </summary>
    /// <remarks>
    /// <b>不会覆盖已有文件</b>：目标路径被占用时自动让开一格（追加 <c>-2</c>、<c>-3</c>…）。
    /// 文件名里没有记录的唯一标识，序列号重复时两条记录会争同一个路径；
    /// 若覆盖后再因入库失败回滚，删掉的就是上一条记录的波形。因此调用方拿到的
    /// <c>RelativePath</c> 未必等于按命名规则推出的那个路径，必须用它返回的这一份。
    /// </remarks>
    Task<(string RelativePath, long FileSize, uint Crc32)> WriteAsync(
        DateTime triggerTime,
        string serialNo,
        int stationId,
        int positionIndex,
        string curveCode,
        CurvePayload payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读回曲线负载。<paramref name="expectedCrc"/> 非空时校验内容，
    /// 不一致抛 <see cref="InvalidDataException"/>（界面据此区分"损坏"与"缺失"）；
    /// 传 null 表示不校验（历史行没有校验值时只能这样）。
    /// </summary>
    Task<CurvePayload> ReadAsync(string relativePath, uint? expectedCrc = null, CancellationToken cancellationToken = default);

    /// <summary>删掉某个已写入的曲线文件（相对路径由 <see cref="WriteAsync"/> 给出）。文件不在时静默返回。</summary>
    Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default);

    Task DeleteMonthAsync(string yyyy, string mm, CancellationToken cancellationToken = default);
}

/// <summary>
/// 文件源工站读到的原始 JSON 的归档。与曲线文件同一套思路：
/// 内容单独落盘、记录里只存 相对路径 + 大小 + CRC32，读取时按 CRC 区分损坏与缺失。
/// </summary>
/// <remarks>
/// 归档的是设备写下的<b>原始字节</b>，不重新序列化：判废争议时它是唯一能证明
/// "设备当时到底写了什么"的东西，任何二次加工都会削弱这个作用。
/// </remarks>
public interface ICollectArchiveStore
{
    /// <summary>
    /// 归档一份原始 JSON，返回实际落盘的相对路径与校验信息。
    /// </summary>
    /// <remarks>
    /// <b>不会覆盖已有文件</b>：目标路径被占用时自动让开一格（追加 <c>-2</c>、<c>-3</c>…）。
    /// 调用方必须使用返回值里的路径，不要自己按命名规则去拼。
    /// </remarks>
    Task<(string RelativePath, long FileSize, uint Crc32)> WriteAsync(
        DateTime triggerTime,
        string palletCode,
        int stationId,
        byte[] content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读回归档。<paramref name="expectedCrc"/> 非空时校验内容，
    /// 不一致抛 <see cref="InvalidDataException"/>（界面据此区分"损坏"与"缺失"）；
    /// 传 null 表示不校验（历史行没有校验值）。
    /// </summary>
    Task<byte[]> ReadAsync(string relativePath, uint? expectedCrc = null, CancellationToken cancellationToken = default);

    /// <summary>删掉某个已归档的文件（相对路径由 <see cref="WriteAsync"/> 给出）。文件不在时静默返回。</summary>
    Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default);

    Task DeleteMonthAsync(string yyyy, string mm, CancellationToken cancellationToken = default);
}

/// <summary>还没补传入库的缓存件数，以及最早一笔的写入时间。</summary>
public readonly record struct SpoolBacklog(int Count, DateTime? OldestAt);

public interface ISpoolStore
{
    Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>只数文件、不读内容。看板和报警轮询用它，避免把每条缓存反序列化一遍。</summary>
    Task<SpoolBacklog> DescribeAsync(CancellationToken cancellationToken = default);
}
