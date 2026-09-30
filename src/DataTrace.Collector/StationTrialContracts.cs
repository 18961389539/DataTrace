using DataTrace.Domain.Entities;
using DataTrace.Plc.Queue;

namespace DataTrace.Collector;

/// <summary>
/// 试读取队列的端口：队列是每台 PLC 的唯一连接收口，试读必须复用采集器现有的那一条。
/// </summary>
/// <remarks>
/// 抽成端口而不是直接依赖采集器宿主，是为了让试读可测 —— 测试给一个假出口即可，
/// 不必为了测一次只读读取而把采集循环整个跑起来。
/// </remarks>
public interface IPlcQueueAccess
{
    /// <summary>取该 PLC 现成的队列；取不到时 <paramref name="reason"/> 说明为什么。</summary>
    bool TryGetTrialQueue(int plcConnectionId, out PlcRequestQueue queue, out string? reason);
}

/// <summary>
/// 一个点位的试读结果：原始字、解码值、生效限值与判定。
/// </summary>
/// <param name="RawText">PLC 源的原始字（十六进制，便于判断字序/字节序）；文件源是字段名。</param>
/// <param name="ReadFailed">没读到（PLC 字块为空 / 文件里没有该字段）。与"超限"是两件事。</param>
public sealed record StationTrialTag(
    string Name,
    string Address,
    bool FromFile,
    string RawText,
    string Display,
    bool ReadFailed,
    bool OutOfLimit,
    bool Warning,
    string LimitText);

/// <summary>触发地址的试读。触发地址不在采集读计划里（只由扫描循环读），所以单独读一次来对照。</summary>
public sealed record StationTrialTrigger(
    string Address,
    short Expected,
    ushort? Value,
    bool Matched,
    string? Error);

/// <summary>一条曲线的试读：字块读齐了没有。只报读没读齐，不画波形。</summary>
public sealed record StationTrialCurve(
    string Name,
    string Address,
    int PointCount,
    int SeriesCount,
    bool ReadOk,
    string Detail);

/// <summary>
/// 一次工站试读的结果。
/// </summary>
/// <remarks>
/// 只读：不落库、不写回、不建会话、不取流水号、不推 MES、不归档文件、不写运行状态。
/// <see cref="Error"/> 非空表示连读都没读成（地址非法 / 队列不可用 / 超时），此时各列表为空。
/// </remarks>
public sealed record StationTrialResult(
    DateTime At,
    long ElapsedMs,
    int BlockCount,
    int WordCount,
    StationTrialTrigger Trigger,
    string? PalletCode,
    bool? Occupied,
    IReadOnlyList<StationTrialTag> Tags,
    IReadOnlyList<StationTrialCurve> Curves,
    string? Error)
{
    public bool Ok => Error is null;

    /// <summary>没读到的点位数。</summary>
    public int ReadFailedCount => Tags.Count(t => t.ReadFailed);

    /// <summary>越过规格的点位数。</summary>
    public int OutOfLimitCount => Tags.Count(t => t.OutOfLimit);
}

/// <summary>
/// 工站试读：按当前配置把该工站的数据只读读一遍，用于验证新配或改过的握手与点位。
/// </summary>
/// <remarks>
/// 它替代不了首件确认 —— 回写响应码、会话关闭、MES 上报内容这三样按定义不会发生，
/// 换型时仍要在停线窗口用一件试件真采一次。试读只回答"我这条地址/字序/限值配对没有"。
/// </remarks>
public interface IStationTrialReader
{
    /// <summary>该 PLC 现在能不能试读。返回 null 表示可以，否则是不能试读的可读原因。</summary>
    string? UnavailableReason(int plcConnectionId);

    /// <summary>
    /// 读一次。任何失败都以 <see cref="StationTrialResult.Error"/> 返回，不抛给调用方 ——
    /// 地址配错正是试读最该被看见的情况，不该变成页面崩溃。
    /// </summary>
    Task<StationTrialResult> ReadAsync(
        Station station,
        PlcConnection connection,
        CancellationToken cancellationToken = default);
}
