using DataTrace.Domain.Enums;

namespace DataTrace.Web.Services;

/// <summary>
/// 按 PLC 品牌生成默认字地址。
/// </summary>
/// <remarks>
/// 新建工站与新建曲线的表单都要预填地址，而地址写法完全由品牌决定：
/// 三菱/欧姆龙用 D 区、西门子用 M 区（字节偏移）、Modbus 用保持寄存器。
/// 两处各写一份的话，改品牌支持时必然漏掉一处，用户看到的就是"预填了一个非法地址"。
/// </remarks>
public static class PlcAddressDefaults
{
    /// <summary>握手区基址：新建工站时触发地址从这里起算。</summary>
    public const int HandshakeBase = 1000;

    /// <summary>曲线 Y 序列的默认起始（避开握手区与工站区）。</summary>
    public const int CurveYOffset = 2000;

    /// <summary>曲线 X 序列的默认起始。</summary>
    public const int CurveXOffset = 2400;

    /// <summary>
    /// 按品牌给出 <paramref name="wordOffset"/> 处的字地址。
    /// </summary>
    /// <remarks>
    /// Modbus 的保持寄存器编号从 40001 起，因此偏移是加在 40001 上的；
    /// 其余品牌直接把偏移当区内的字偏移写（D2000、MW2000…）。
    /// </remarks>
    public static string Word(PlcBrand brand, int wordOffset)
        => brand switch
        {
            PlcBrand.SiemensS7 => $"MW{wordOffset}",
            PlcBrand.ModbusTcp => (40001 + wordOffset).ToString(),
            PlcBrand.OmronFins => $"D{wordOffset}",
            _ => $"D{wordOffset}"
        };
}