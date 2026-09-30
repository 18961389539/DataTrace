using DataTrace.Application.Alarms;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Realtime;

public sealed class StationRuntimeStatus
{
    public int StationId { get; init; }
    public string StationCode { get; init; } = "";
    public string StationName { get; init; } = "";
    public int Sequence { get; init; }
    public StationRuntimeState State { get; set; }
    public string? LastPalletCode { get; set; }
    public string? LastSerialNo { get; set; }
    public short? LastResultCode { get; set; }
    public Judgement LastJudgement { get; set; }
    public DateTime? LastCompleteTime { get; set; }

    /// <summary>同一工站最近相邻两件完成相隔的时间。只有一件时还没有间隔。</summary>
    public TimeSpan? LastPieceGap { get; set; }

    public int? LastDurationMs { get; set; }
    public string? LastError { get; set; }
    public string? LastMonthKey { get; set; }
    public long? LastRecordId { get; set; }

    /// <summary>
    /// 最近一次响应码回写用了几次尝试（1 = 一次就成）。0 表示还没写过。
    /// </summary>
    /// <remarks>
    /// 回写发生在落库之后，所以它进不了那条记录，只能作为工站的当前状态保留：
    /// "写回重试 3 次才成功"是链路正在变坏的早期信号，比等到回写彻底失败才报警早得多。
    /// </remarks>
    public int LastWriteBackAttempts { get; set; }

    /// <summary>最近一次响应码回写是否成功。false 时工站会被标成故障。</summary>
    public bool LastWriteBackOk { get; set; }

    public IReadOnlyList<StationLiveTag> LastTags { get; set; } = [];
    public IReadOnlyList<StationLiveCurve> LastCurves { get; set; } = [];
}

public sealed class StationLiveTag
{
    public required string Name { get; init; }
    public required string Display { get; init; }
    public string? Unit { get; init; }
    public double? NumericValue { get; init; }
    public double? LowerLimit { get; init; }
    public double? UpperLimit { get; init; }
    public double? WarningLowerLimit { get; init; }
    public double? WarningUpperLimit { get; init; }

    /// <summary>超出规格限（红区），该点位的记录会判废。</summary>
    public bool OutOfLimit { get; init; }

    /// <summary>落在预警带（黄区）。不判废，用于看板早期提示。</summary>
    public bool Warning { get; init; }
}

public sealed class StationLiveCurve
{
    public required string Name { get; init; }
    public required float[] Values { get; init; }
}

public sealed class CollectFeedItem
{
    public required string MonthKey { get; init; }
    public long RecordId { get; init; }
    public required string StationCode { get; init; }
    public required string PalletCode { get; init; }
    public required string SerialNo { get; init; }
    /// <summary>采集时生效的产品型号编码；空表示当时未选型号。</summary>
    public string RecipeCode { get; init; } = "";
    public short ResultCode { get; init; }
    public Judgement Judgement { get; init; }
    public DateTime CompleteTime { get; init; }
    public int DurationMs { get; init; }
    public string? Error { get; init; }
}

public sealed class PlcRuntimeStatus
{
    public int PlcConnectionId { get; init; }
    public string Name { get; init; } = "";
    public bool Connected { get; set; }
    public string? LastError { get; set; }
}

public interface IRuntimeStatusHub
{
    IReadOnlyList<StationRuntimeStatus> Stations { get; }
    IReadOnlyList<PlcRuntimeStatus> Plcs { get; }
    IReadOnlyList<CollectFeedItem> Recent { get; }

    /// <summary>当前生效型号编码；null/空表示未选择（按点位默认限值）。</summary>
    string? ActiveRecipeCode { get; }

    /// <summary>当前生效型号名称；未选择时为 null。</summary>
    string? ActiveRecipeName { get; }

    event Action? Changed;
    void UpsertStation(StationRuntimeStatus status);
    void UpsertPlc(PlcRuntimeStatus status);

    /// <summary>
    /// 更新当前型号指示（配置切换或采集器加载快照时调用）。
    /// 值变化时触发 <see cref="Changed"/>，供看板等页面热刷新。
    /// </summary>
    void SetActiveRecipe(string? code, string? name);

    /// <summary>
    /// 采集器主循环心跳，取本机 <see cref="DateTime.Now"/>。看板据此判断数据是否停更；
    /// 仅更新时间戳，不触发 <see cref="Changed"/>，避免每拍刷屏。
    /// </summary>
    DateTime LastCollectorAt { get; }

    /// <summary>
    /// 各工站当前连续不合格件数，质量不合格和采集失败或跳站分开计。
    /// 只活在本进程：合格把两串都清掉，未判定不改变计数。
    /// 进程启动时由当月记录重算一次；最近记录和心跳不恢复。
    /// </summary>
    IReadOnlyList<StationNgStreak> NgStreaks { get; }

    /// <summary>重算完成前一直挂起。后来的调用方等这一次，不再自己读库。</summary>
    Task NgStreaksReady { get; }

    /// <summary>抢到唯一的一次重算权。没抢到就等 <see cref="NgStreaksReady"/>。</summary>
    bool TryClaimNgStreakRestore();

    /// <summary>用当月重算结果替换连续 NG。最近记录和心跳保持不动。</summary>
    void CompleteNgStreakRestore(IReadOnlyList<StationNgStreak> streaks);

    /// <summary>各点位当前连续落在预警带的件数。超限或回到规格内会清掉。</summary>
    IReadOnlyList<TagWarningStreak> WarningStreaks { get; }

    /// <summary>控制图此刻仍命中、并且延伸到最新一点的过程漂移。</summary>
    IReadOnlyList<TagDriftNotice> DriftNotices { get; }

    /// <summary>用最近的点位观测重算预警连续件数和过程漂移窗口。</summary>
    void CompleteTagWatchRestore(IReadOnlyList<TagObservation> observations);

    /// <summary>超时还没走到末站的在制托盘。由报警轮询写入，看板直接读。</summary>
    IReadOnlyList<OpenSessionNotice> OpenSessions { get; }

    void ReplaceOpenSessions(IReadOnlyList<OpenSessionNotice> sessions);

    /// <summary>线上每一件进首站时记下的型号。由报警轮询写入，看板用来标明换型后旧件仍用哪套。</summary>
    IReadOnlyList<InProcessRecipe> InProcessRecipes { get; }

    void ReplaceInProcessRecipes(IReadOnlyList<InProcessRecipe> recipes);

    /// <summary>重算失败时放行等待方，连续 NG 从 0 开始。</summary>
    void AbandonNgStreakRestore();

    /// <summary>采集器每次循环调用；不广播 Changed。</summary>
    void NoteCollectorTick();

    /// <summary>强制通知订阅方刷新（例如仅改了 CollectEnabled）。</summary>
    void NotifyChanged();
}

public interface ICollectEventBus
{
    event Action<CollectRecord>? RecordSaved;
    void Publish(CollectRecord record);
}
