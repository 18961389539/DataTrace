using System.Text;
using DataTrace.Domain.Entities;

namespace DataTrace.Collector;

/// <summary>
/// PLC 连接签名：只涵盖"影响驱动与读计划"的字段。
/// </summary>
/// <remarks>
/// 采集器每轮都要判断配置是否变了。若只看配置版本号，那么随便改一个点位（版本号也会 +1）
/// 就会把全部 PLC 连接拆掉重建 —— 表现为正在生产的产线全体断线重连。
/// 有了签名就能只重建真正变了的那台，其余连接原地不动。
/// </remarks>
public static class PlcConnectionSignature
{
    public static string Of(PlcConnection plc)
    {
        var heartbeat = plc.Heartbeat is { } hb
            ? $"{hb.Enabled}|{hb.Address}|{hb.IntervalMs}|{hb.Mode}"
            : "-";

        // Name 也算进签名：状态面板显示的是它，改名后就该重新登记一次状态。
        return new StringBuilder()
            .Append(plc.Id).Append(';')
            .Append(plc.Name).Append(';')
            .Append(plc.Brand).Append(';')
            .Append(plc.Host).Append(':').Append(plc.Port).Append(';')
            .Append(plc.TimeoutMs).Append(';')
            .Append(plc.FloatWordOrder).Append(';')
            .Append(plc.StringHighByteFirst).Append(';')
            .Append(plc.MergeGapWords).Append(';')
            .Append(plc.Enabled).Append(';')
            .Append(plc.Extra).Append(';')
            .Append(heartbeat)
            .ToString();
    }
}