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
    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

public sealed class CollectRecordListItem
{
    public required string MonthKey { get; init; }
    public required CollectRecord Record { get; init; }
}

public sealed class CollectQueryResult
{
    public required int Total { get; init; }
    public required IReadOnlyList<CollectRecordListItem> Items { get; init; }
}

/// <summary>
/// 吞吐量统计的窄投影：只带时间、判定与所属工站。
/// 报表只需要计数，没必要把记录整图（含 Products / TagValues）拉进内存。
/// </summary>
public sealed class JudgementPoint
{
    public DateTime Time { get; init; }
    public int StationId { get; init; }
    public Judgement Judgement { get; init; }
}

/// <summary>不良 / 预警统计的窄投影：只带点位名称与代码。</summary>
public sealed class TagIssuePoint
{
    public string TagName { get; init; } = "";
    public string TagCode { get; init; } = "";
}

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

    /// <summary>所属采集记录的判定；基线只用合格样本建立。</summary>
    public bool IsNg { get; init; }

    /// <summary>该曲线判定时生效的产品型号；用于避免切换型号后基线整体失配。</summary>
    public string RecipeCode { get; init; } = "";

    public string SeriesName { get; init; } = "";

    public SeriesRole Role { get; init; }

    public CurveFeature Feature { get; init; } = new();
}

public interface IRuntimeStore
{
    Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default);
    Task<CollectQueryResult> QueryAsync(CollectQueryRequest request, CancellationToken cancellationToken = default);
    Task<CollectRecord?> GetRecordAsync(string monthKey, long recordId, CancellationToken cancellationToken = default);
    Task<PalletSession?> GetSessionAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CollectRecord>> GetSessionRecordsAsync(string monthKey, long sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CollectRecord>> QueryForReportAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>吞吐量统计的窄投影查询（服务端过滤 + 只取三列）。</summary>
    Task<IReadOnlyList<JudgementPoint>> QueryJudgementPointsAsync(DateTime from, DateTime to, int? stationId, CancellationToken cancellationToken = default);

    /// <summary>不良点位的窄投影查询（服务端按 IsOutOfLimit 过滤）。</summary>
    Task<IReadOnlyList<TagIssuePoint>> QueryOutOfLimitTagsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>预警点位的窄投影查询（服务端按 IsWarning 过滤）。</summary>
    Task<IReadOnlyList<TagIssuePoint>> QueryWarningTagsAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default);

    /// <summary>单点位趋势的窄投影查询（服务端按 TagId 过滤）。</summary>
    Task<IReadOnlyList<TagTrendPoint>> QueryTagTrendAsync(DateTime from, DateTime to, int tagId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 波形特征的窄投影查询：取某条曲线某个序列在区间内<b>最新的 take 条</b>。
    /// 基线应当反映设备"最近"的正常状态，而不是整段历史的平均，
    /// 因此用 take 限制样本量而不是把区间内全部特征拉进内存。
    /// </summary>
    /// <param name="seriesName">序列名；null 表示该曲线的全部序列。</param>
    Task<IReadOnlyList<CurveFeaturePoint>> QueryCurveFeaturesAsync(
        int curveDefinitionId,
        string? seriesName,
        DateTime from,
        DateTime to,
        int take,
        CancellationToken cancellationToken = default);

    Task MarkSessionAbnormalAsync(string monthKey, long sessionId, DateTime endTime, CancellationToken cancellationToken = default);
    Task DeleteMonthAsync(string monthKey, CancellationToken cancellationToken = default);
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
    Task<(string RelativePath, long FileSize, uint Crc32)> WriteAsync(
        DateTime triggerTime,
        string serialNo,
        int stationId,
        int positionIndex,
        string curveCode,
        CurvePayload payload,
        CancellationToken cancellationToken = default);

    Task<CurvePayload> ReadAsync(string relativePath, CancellationToken cancellationToken = default);
    Task DeleteMonthAsync(string yyyy, string mm, CancellationToken cancellationToken = default);
}

public interface ISpoolStore
{
    Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListAsync(CancellationToken cancellationToken = default);
    Task DeleteAsync(string fileName, CancellationToken cancellationToken = default);
}
