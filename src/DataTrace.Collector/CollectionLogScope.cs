namespace DataTrace.Collector;

/// <summary>
/// 采集链路日志作用域的属性名。
/// </summary>
/// <remarks>
/// 常量集中在这里，是因为作用域全靠"名字一致"才串得起来：
/// 一处写 <c>CollectStation</c>、另一处写 <c>Station</c>，日志里就会冒出两组属性，谁也串不上谁。
/// 这些名字会随 appsettings 里 Serilog 输出模板的 <c>{Properties}</c> 一起落到日志文件上。
/// </remarks>
public static class CollectionLogScope
{
    /// <summary>工站编码。</summary>
    public const string Station = "CollectStation";

    /// <summary>这一件的触发时刻（含毫秒）。同一工站同一毫秒触发两次时，它是唯一还能区分的字段。</summary>
    public const string Trigger = "CollectTrigger";

    /// <summary>
    /// 流水号。
    /// </summary>
    /// <remarks>
    /// 首站要在建会话时才知道流水号，所以它比工站和触发时刻晚：只出现在落库及其之后的日志上。
    /// 后段（落库、补传、MES）也是现场最常问的那一段，所以它恰恰是最该带上的一个。
    /// </remarks>
    public const string Serial = "CollectSerial";
}
