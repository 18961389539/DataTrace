using System.Text.RegularExpressions;

namespace DataTrace.Plc.Addresses;

/// <summary>Modbus：00001/10001/30001/40001，或 0x/1x/3x/4x 前缀。</summary>
public sealed class ModbusAddressParser : IAddressParser
{
    private static readonly Regex PrefixPattern = new(
        @"^(?<prefix>[0134])x(?<offset>\d{1,5})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool TryParse(string text, out PlcAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim().ToUpperInvariant().Replace(" ", "");
        var prefixed = PrefixPattern.Match(raw);
        if (prefixed.Success)
        {
            var offset = int.Parse(prefixed.Groups["offset"].Value);
            var area = AreaOf(prefixed.Groups["prefix"].Value);
            var kind = area is "COIL" or "DISCRETE" ? AddressKind.Bit : AddressKind.Word;
            address = new PlcAddress(area, offset, kind == AddressKind.Bit ? 0 : -1, kind, raw);
            return true;
        }

        if (!int.TryParse(raw, out var offsetRaw) || offsetRaw < 0)
        {
            return false;
        }

        string mappedArea;
        int mappedOffset;
        AddressKind mappedKind;
        switch (offsetRaw)
        {
            case >= 40001:
                mappedArea = "HOLDING";
                mappedOffset = offsetRaw - 40001;
                mappedKind = AddressKind.Word;
                break;
            case >= 30001:
                mappedArea = "INPUT";
                mappedOffset = offsetRaw - 30001;
                mappedKind = AddressKind.Word;
                break;
            case >= 10001:
                mappedArea = "DISCRETE";
                mappedOffset = offsetRaw - 10001;
                mappedKind = AddressKind.Bit;
                break;
            default:
                mappedArea = "COIL";
                mappedOffset = Math.Max(0, offsetRaw - 1);
                mappedKind = AddressKind.Bit;
                break;
        }

        address = new PlcAddress(mappedArea, mappedOffset, mappedKind == AddressKind.Bit ? 0 : -1, mappedKind, raw);
        return true;
    }

    private static string AreaOf(string prefix) => prefix switch
    {
        "0" => "COIL",
        "1" => "DISCRETE",
        "3" => "INPUT",
        _ => "HOLDING"
    };
}
