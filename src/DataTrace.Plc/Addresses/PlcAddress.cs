namespace DataTrace.Plc.Addresses;

public enum AddressKind
{
    Word = 0,
    Bit = 1
}

public sealed record PlcAddress(
    string Area,
    int Offset,
    int BitIndex,
    AddressKind Kind,
    string Original)
{
    public bool IsBit => Kind == AddressKind.Bit;
}
