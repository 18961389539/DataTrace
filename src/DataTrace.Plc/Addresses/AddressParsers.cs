using DataTrace.Domain.Enums;
using DataTrace.Plc.Simulator;

namespace DataTrace.Plc.Addresses;

/// <summary>
/// 品牌到地址解析器的映射。
/// </summary>
/// <remarks>
/// 配置页要在保存前校验地址，采集侧要解析地址，两边必须用同一套语法；
/// 这里做成唯一来源，避免两处各写一份 switch 后慢慢长歪。
/// </remarks>
public static class AddressParsers
{
    public static IAddressParser For(PlcBrand brand) => brand switch
    {
        PlcBrand.SiemensS7 => new SiemensAddressParser(),
        PlcBrand.ModbusTcp => new ModbusAddressParser(),
        PlcBrand.OmronFins => new OmronAddressParser(),
        PlcBrand.Simulator => new SimulatorAddressParser(),
        _ => new MitsubishiAddressParser()
    };
}
