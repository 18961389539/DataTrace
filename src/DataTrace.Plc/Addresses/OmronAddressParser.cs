using System.Text.RegularExpressions;

namespace DataTrace.Plc.Addresses;

/// <summary>欧姆龙 FINS：D100、CIO0.0、W0、H10、A100</summary>
public sealed class OmronAddressParser : IAddressParser
{
    private static readonly Regex Pattern = new(
        @"^(?<area>CIO|WR|HR|AR|DM|D|W|H|A)(?<offset>\d+)(?:\.(?<bit>\d+))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        var area = Normalize(match.Groups["area"].Value);
        var offset = int.Parse(match.Groups["offset"].Value);
        if (match.Groups["bit"].Success)
        {
            var bit = int.Parse(match.Groups["bit"].Value);
            address = new PlcAddress(area, offset, bit, AddressKind.Bit, raw);
            return true;
        }

        address = new PlcAddress(area, offset, -1, AddressKind.Word, raw);
        return true;
    }

    private static string Normalize(string area) => area switch
    {
        "D" => "DM",
        "W" => "WR",
        "H" => "HR",
        "A" => "AR",
        _ => area
    };
}
