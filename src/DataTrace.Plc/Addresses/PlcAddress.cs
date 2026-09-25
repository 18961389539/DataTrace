namespace DataTrace.Plc.Addresses;

public enum AddressKind
{
    Word = 0,
    Bit = 1
}

/// <summary>
/// <see cref="PlcAddress.Offset"/> 的计量单位。
/// </summary>
/// <remarks>
/// 三菱/欧姆龙/Modbus 的偏移都是"第几个字"（<see cref="Word"/>）；
/// 西门子的偏移在 PLC 侧本来就是字节（DB108.DBW10 指的是 DB108 的第 10 个字节），
/// 且 IoTClient 的西门子驱动也按字节解析地址。两个单位混在同一套偏移算术里会让
/// 读计划算错切片位置，所以必须显式区分。
/// </remarks>
public enum OffsetUnit
{
    Word = 0,
    Byte = 1
}

public sealed record PlcAddress(
    string Area,
    int Offset,
    int BitIndex,
    AddressKind Kind,
    string Original,
    OffsetUnit OffsetUnit = OffsetUnit.Word)
{
    public bool IsBit => Kind == AddressKind.Bit;
}
