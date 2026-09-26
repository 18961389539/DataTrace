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

/// <summary>
/// 点位的取值来源：PLC 寄存器，或工站那一个数据文件。
/// </summary>
/// <remarks>
/// 触发、托盘码与曲线不受它影响 —— 换的只是"这个点位的值从哪读"。
/// 枚举值直接落库（Tags.Source），改动即破坏兼容。
/// 文件是 JSON 还是 CSV 不在这里，见 <see cref="DataFileFormat"/>。
/// </remarks>
public enum TagDataSource
{
    Plc = 0,

    /// <summary>点位值取自工站数据文件；路径与格式见工站。</summary>
    JsonFile = 1
}

/// <summary>
/// 工站数据文件的格式。0 是 JSON，老配置库补列后仍按 JSON 读。
/// </summary>
public enum DataFileFormat
{
    Json = 0,
    Csv = 1
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
