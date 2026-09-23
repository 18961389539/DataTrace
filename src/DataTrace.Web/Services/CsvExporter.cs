using System.Globalization;
using System.Text;

namespace DataTrace.Web.Services;

/// <summary>
/// CSV 导出工具：统一处理转义与 UTF-8 BOM，保证 Excel 打开中文不乱码。
/// </summary>
public static class CsvExporter
{
    /// <summary>把表头与数据行拼成 CSV 文本（不含 BOM）。</summary>
    public static string Build(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows)
    {
        var builder = new StringBuilder();
        builder.Append(string.Join(',', headers.Select(Escape)));
        foreach (var row in rows)
        {
            builder.Append('\n');
            builder.Append(string.Join(',', row.Select(Escape)));
        }

        return builder.ToString();
    }

    /// <summary>把 CSV 文本转成带 BOM 的 Base64，交给前端 Blob 下载。</summary>
    public static string ToBase64(string csv)
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF" + csv);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>按不变文化输出数值，避免与 CSV 分隔符冲突。</summary>
    public static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>导出文件名统一带时间戳，避免多次导出互相覆盖。</summary>
    public static string FileName(string prefix) => $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

    private static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.IndexOfAny([',', '"', '\n', '\r']) < 0)
        {
            return text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
