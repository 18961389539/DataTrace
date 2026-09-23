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
    event Action? Changed;
    void UpsertStation(StationRuntimeStatus status);
    void UpsertPlc(PlcRuntimeStatus status);
}

public interface ICollectEventBus
{
    event Action<CollectRecord>? RecordSaved;
    void Publish(CollectRecord record);
}
