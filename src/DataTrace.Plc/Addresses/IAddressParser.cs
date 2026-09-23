namespace DataTrace.Plc.Addresses;

public interface IAddressParser
{
    bool TryParse(string text, out PlcAddress address);
}
