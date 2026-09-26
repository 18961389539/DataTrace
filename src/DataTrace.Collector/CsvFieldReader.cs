using System.Globalization;
using System.Text;

namespace DataTrace.Collector;

/// <summary>
/// 从设备覆写的 CSV 里按表头列名取一个单元格。
/// </summary>
/// <remarks>
/// 只认 UTF-8（可带 BOM）、逗号分隔、第一行表头、下面恰好一行数据。
/// 多行数据直接失败：设备每件覆写同一个文件，多出来的行分不清是这一件还是上一件。
/// 数值口径与 <see cref="JsonFieldReader"/> 相同：数字或数字串，不认带单位的文本。
/// </remarks>
public static class CsvFieldReader
{
    public static bool TryParse(byte[] bytes, out Dictionary<string, string> row, out string? error)
    {
        row = new Dictionary<string, string>(StringComparer.Ordinal);
        string text;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            error = "不是 UTF-8 文本（请把 CSV 另存为 UTF-8）";
            return false;
        }

        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        List<List<string>> records;
        try
        {
            records = ParseRecords(text);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        var rows = records.Where(record => record.Any(cell => cell.Length > 0)).ToList();
        if (rows.Count == 0)
        {
            error = "没有表头";
            return false;
        }

        var header = rows[0].Select(cell => cell.Trim()).ToList();
        if (header.Any(name => name.Length == 0))
        {
            error = "表头有空列名";
            return false;
        }

        if (header.Distinct(StringComparer.Ordinal).Count() != header.Count)
        {
            error = "表头列名重复";
            return false;
        }

        if (rows.Count == 1)
        {
            error = null;
            return true;
        }

        if (rows.Count > 2)
        {
            error = $"只能有表头和一行数据，当前有 {rows.Count - 1} 行";
            return false;
        }

        var data = rows[1];
        for (var i = 0; i < header.Count; i++)
        {
            row[header[i]] = i < data.Count ? data[i] : "";
        }

        error = null;
        return true;
    }

    public static bool TryReadNumeric(IReadOnlyDictionary<string, string> row, string column, out double value)
    {
        value = 0;
        if (!TryCell(row, column, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool TryReadText(IReadOnlyDictionary<string, string> row, string column, out string text)
    {
        if (!TryCell(row, column, out var cell) || cell is null)
        {
            text = "";
            return false;
        }

        text = cell;
        return true;
    }

    private static bool TryCell(IReadOnlyDictionary<string, string> row, string column, out string? text)
    {
        text = null;
        if (string.IsNullOrWhiteSpace(column))
        {
            return false;
        }

        return row.TryGetValue(column.Trim(), out text);
    }

    private static List<List<string>> ParseRecords(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
                continue;
            }

            if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                records.Add(row);
                row = [];
                continue;
            }

            field.Append(c);
            fieldStarted = true;
        }

        if (inQuotes)
        {
            throw new FormatException("引号没有闭合");
        }

        if (fieldStarted || row.Count > 0)
        {
            row.Add(field.ToString());
            records.Add(row);
        }

        return records;
    }
}
