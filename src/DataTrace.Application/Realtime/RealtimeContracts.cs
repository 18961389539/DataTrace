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
    public int? LastDurationMs { get; set; }
    public string? LastError { get; set; }
    public string? LastMonthKey { get; set; }
    public long? LastRecordId { get; set; }
    public IReadOnlyList<StationLiveTag> LastTags { get; set; } = [];
    public IReadOnlyList<StationLiveCurve> LastCurves { get; set; } = [];
}

public sealed class StationLiveTag
{
    public required string Name { get; init; }
    public required string Display { get; init; }
    public string? Unit { get; init; }

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
    /// 采集器主循环心跳时间（UTC）。看板据此判断数据是否停更；
    /// 仅更新时间戳，不触发 <see cref="Changed"/>，避免每拍刷屏。
    /// </summary>
    DateTime LastCollectorUtc { get; }

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
