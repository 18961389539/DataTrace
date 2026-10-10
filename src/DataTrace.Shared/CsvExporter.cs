using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;

namespace DataTrace.Shared;

/// <summary>
/// CSV 导出工具：统一处理转义与 UTF-8 BOM，保证 Excel 打开中文不乱码。
/// </summary>
public static class CsvExporter
{
    /// <summary>把表头与数据行拼成 CSV 文本（不含 BOM）。</summary>
    public static string Build(IEnumerable<string> headers, IEnumerable<IEnumerable<string?>> rows)
    {
        var builder = new StringBuilder(WriteRecord(headers));
        foreach (var row in rows)
        {
            builder.Append('\n');
            builder.Append(WriteRecord(row));
        }

        return builder.ToString();
    }

    /// <summary>转义并输出单行 CSV，供大文件导出逐行写入流。</summary>
    public static string BuildRow(IEnumerable<string?> values)
        => WriteRecord(values);

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

    private static string WriteRecord(IEnumerable<string?> values)
    {
        using var textWriter = new StringWriter(CultureInfo.InvariantCulture);
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Mode = CsvMode.RFC4180,
            NewLine = "\n",
            ShouldQuote = args => args.Field is not null && args.Field.IndexOfAny([',', '"', '\r', '\n']) >= 0
        };
        using (var csv = new CsvWriter(textWriter, configuration))
        {
            foreach (var value in values)
            {
                var text = value ?? "";
                // Excel 会执行以 =、+、@ 开头的单元格；负号保留给正常负数。
                if (text.Length > 0 && text[0] is '=' or '+' or '@')
                {
                    text = "'" + text;
                }

                csv.WriteField(text);
            }

            csv.NextRecord();
        }

        var record = textWriter.ToString();
        return record.EndsWith('\n') ? record[..^1] : record;
    }
}
