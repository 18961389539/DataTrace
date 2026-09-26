using System.Globalization;
using System.Text.Json;

namespace DataTrace.Collector;

/// <summary>
/// 文件源点位的取值口径：从设备写下的 JSON 里按字段路径取一个数或一段文本。
/// </summary>
/// <remarks>
/// 只认「数字」或「数字串」，<b>不认单位、不套 PLC 缩放系数</b>：
/// 文件里的值已经是工程量，而带单位的文本（"12 kN"）本该是配置错字段，
/// 静默按前缀解析出一个数比直接取不到更危险。
/// </remarks>
public static class JsonFieldReader
{
    /// <summary>按 <c>a.b.c</c> 的路径逐层进对象取字段；路径写错、中途不是对象都返回 false。</summary>
    public static bool TryResolve(JsonElement root, string fieldPath, out JsonElement value)
    {
        value = default;
        if (root.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(fieldPath))
        {
            return false;
        }

        var current = root;
        foreach (var segment in fieldPath.Trim().Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return false;
            }

            current = next;
        }

        value = current;
        return true;
    }

    /// <summary>取数值：JSON 数字或数字串都行；其它类型（含 null、bool、对象、数组）一律算取不到。</summary>
    public static bool TryReadNumeric(JsonElement root, string fieldPath, out double value)
    {
        value = 0;
        if (!TryResolve(root, fieldPath, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    /// <summary>取文本：字符串原样返回；数字按原文返回（避免"读到 12 变成 12.0"）。</summary>
    public static bool TryReadText(JsonElement root, string fieldPath, out string text)
    {
        text = "";
        if (!TryResolve(root, fieldPath, out var element))
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text = element.GetString() ?? "";
                return true;
            case JsonValueKind.Number:
                text = element.GetRawText();
                return true;
            case JsonValueKind.True:
            case JsonValueKind.False:
                text = element.GetRawText();
                return true;
            default:
                return false;
        }
    }
}