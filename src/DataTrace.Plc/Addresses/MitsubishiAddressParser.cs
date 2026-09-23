using System.Text.RegularExpressions;

namespace DataTrace.Plc.Addresses;

/// <summary>三菱 MC 地址：D100、M100、X1A、Y0、W1FF、ZR100、SD10、D100.0</summary>
public sealed class MitsubishiAddressParser : IAddressParser
{
    private static readonly Regex Pattern = new(
        @"^(?<area>ZR|SD|SM|SB|SW|CN|CC|CS|TN|TC|TS|D|W|R|M|X|Y|B|F|L)(?<offset>[0-9A-Fa-f]+)(?:\.(?<bit>[0-9A-Fa-f]+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> HexAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "X", "Y", "B", "W", "SB", "SW"
    };

    private static readonly HashSet<string> BitAreas = new(StringComparer.OrdinalIgnoreCase)
    {
        "M", "X", "Y", "B", "SM", "SB", "F", "L", "CC", "CS", "TC", "TS"
    };

    public bool TryParse(string text, out PlcAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim().ToUpperInvariant();
        var match = Pattern.Match(raw);
        if (!match.Success)
        {
            return false;
        }

        var area = match.Groups["area"].Value.ToUpperInvariant();
        var offsetText = match.Groups["offset"].Value;
        var fromHex = HexAreas.Contains(area);
        if (!TryParseNumber(offsetText, fromHex, out var offset))
        {
            return false;
        }

        var bitGroup = match.Groups["bit"];
        if (bitGroup.Success)
        {
            if (!TryParseNumber(bitGroup.Value, fromHex, out var bit) || bit < 0 || bit > 15)
            {
                return false;
            }

            address = new PlcAddress(area, offset, bit, AddressKind.Bit, raw);
            return true;
        }

        var kind = BitAreas.Contains(area) ? AddressKind.Bit : AddressKind.Word;
        address = new PlcAddress(area, offset, kind == AddressKind.Bit ? 0 : -1, kind, raw);
        return true;
    }

    private static bool TryParseNumber(string text, bool hex, out int value)
    {
        return hex
            ? int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value)
            : int.TryParse(text, out value);
    }
}
