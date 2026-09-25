using System.Text.RegularExpressions;

namespace DataTrace.Plc.Addresses;

/// <summary>西门子 S7：DB1.DBW0、DB1.DBD2、DB1.DBX0.0、MW10、MD20、MB5、I0.0、Q0.1、M10.2</summary>
public sealed class SiemensAddressParser : IAddressParser
{
    private static readonly Regex DbPattern = new(
        @"^DB(?<db>\d+)\.DB(?<kind>X|B|W|D)(?<offset>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SimplePattern = new(
        @"^(?<area>I|E|Q|A|M|V)(?<kind>B|W|D)?(?<offset>\d+)(?:\.(?<bit>[0-7]))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public bool TryParse(string text, out PlcAddress address)
    {
        address = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var raw = text.Trim().ToUpperInvariant();
        var db = DbPattern.Match(raw);
        if (db.Success)
        {
            var dbNumber = int.Parse(db.Groups["db"].Value);
            var kindChar = db.Groups["kind"].Value.ToUpperInvariant();
            var offset = int.Parse(db.Groups["offset"].Value);
            var area = $"DB{dbNumber}";
            if (kindChar == "X")
            {
                if (!db.Groups["bit"].Success)
                {
                    return false;
                }

                var bit = int.Parse(db.Groups["bit"].Value);
                address = new PlcAddress(area, offset, bit, AddressKind.Bit, raw, OffsetUnit.Byte);
                return true;
            }

            // S7 的偏移是字节：DB1.DBW10 指第 10 个字节，而不是第 10 个字。
            // 内部按字读时靠 OffsetUnit.Byte 让读计划按字节步进，避免把偏移当字数算。
            address = new PlcAddress(area, offset, -1, AddressKind.Word, raw, OffsetUnit.Byte);
            return true;
        }

        var simple = SimplePattern.Match(raw);
        if (!simple.Success)
        {
            return false;
        }

        var areaName = NormalizeArea(simple.Groups["area"].Value);
        var offset2 = int.Parse(simple.Groups["offset"].Value);
        if (simple.Groups["bit"].Success)
        {
            var bit = int.Parse(simple.Groups["bit"].Value);
            address = new PlcAddress(areaName, offset2, bit, AddressKind.Bit, raw, OffsetUnit.Byte);
            return true;
        }

        // 与 DB 区同理：MW10 / MB5 / M10 里的数字都是字节偏移。
        address = new PlcAddress(areaName, offset2, -1, AddressKind.Word, raw, OffsetUnit.Byte);
        return true;
    }

    private static string NormalizeArea(string area) => area switch
    {
        "E" => "I",
        "A" => "Q",
        _ => area
    };
}
