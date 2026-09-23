using System.Text.RegularExpressions;

namespace DataTrace.Domain.Validation;

public static class PalletCodeValidator
{
    private static readonly Regex Allowed = new(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.Compiled);

    public static bool IsValid(string? palletCode, out string error)
    {
        if (string.IsNullOrWhiteSpace(palletCode))
        {
            error = "托盘码为空";
            return false;
        }

        var trimmed = palletCode.Trim().Trim('\0');
        if (!Allowed.IsMatch(trimmed))
        {
            error = "托盘码含非法字符或长度不符";
            return false;
        }

        error = "";
        return true;
    }
}
