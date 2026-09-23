namespace DataTrace.Domain.Enums;

public enum PlcBrand
{
    Simulator = 0,
    MitsubishiMc3E = 1,
    SiemensS7 = 2,
    ModbusTcp = 3,
    OmronFins = 4
}

public enum PlcDataType
{
    Bool = 0,
    Int16 = 1,
    Int32 = 2,
    Float = 3,
    Double = 4,
    String = 5
}

/// <summary>
/// IEEE-754 单精度在 PLC 两个字中的字节排列。A 为最高字节。
/// </summary>
public enum FloatWordOrder
{
    ABCD = 0,
    BADC = 1,
    CDAB = 2,
    DCBA = 3
}

public enum SeriesRole
{
    X = 0,
    Y = 1
}

public enum SessionStatus
{
    Open = 0,
    Closed = 1,
    Abnormal = 2
}

public enum Judgement
{
    None = 0,
    Ok = 1,
    Ng = 2
}

public enum HeartbeatMode
{
    Increment = 0,
    Toggle = 1
}

public enum MesOutboxStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}

public enum StationRuntimeState
{
    Idle = 0,
    Busy = 1,
    Fault = 2,
    Disabled = 3
}

/// <summary>
/// 点位限值判定结果。只有 <see cref="OutOfSpec"/> 会判废；
/// <see cref="Warning"/> 表示落进预警带（黄区），只提示、不影响合格判定与 PLC 响应码。
/// </summary>
/// <remarks>数值直接落库，改动即破坏兼容。</remarks>
public enum LimitStatus
{
    /// <summary>未配置任何限值，无从判定。</summary>
    None = 0,

    /// <summary>在规格内。</summary>
    InSpec = 1,

    /// <summary>落在预警带（黄区）。</summary>
    Warning = 2,

    /// <summary>超出规格限（或必填点位取空），判废。</summary>
    OutOfSpec = 3
}
