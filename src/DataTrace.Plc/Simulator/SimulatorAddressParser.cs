using System.Text.RegularExpressions;
using DataTrace.Plc.Addresses;

namespace DataTrace.Plc.Simulator;

/// <summary>
/// The simulator accepts legacy Mitsubishi word areas, MW word indexes, and MD
/// double-word indexes. MD indexes map to two internal words; optional '%' is accepted.
/// </summary>
public sealed class SimulatorAddressParser : IAddressParser
{
    private static readonly Regex MemoryWordPattern = new(
        @"^MW(?<offset>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MemoryDoubleWordPattern = new(
        @"^%?MD(?<offset>\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly MitsubishiAddressParser _mitsubishi = new();

    public bool TryParse(string text, out PlcAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim().ToUpperInvariant();
        var match = MemoryWordPattern.Match(raw);
        if (match.Success
            && int.TryParse(match.Groups["offset"].Value, out var offset))
        {
            address = new PlcAddress("MW", offset, -1, AddressKind.Word, raw);
            return true;
        }

        match = MemoryDoubleWordPattern.Match(raw);
        if (match.Success
            && int.TryParse(match.Groups["offset"].Value, out offset)
            && offset <= int.MaxValue / 2)
        {
            address = new PlcAddress("MD", offset * 2, -1, AddressKind.Word, raw);
            return true;
        }

        return _mitsubishi.TryParse(raw, out address);
    }
}
