using System.Text.Json;

namespace DataTrace.Web.Services;

public sealed record AuditFieldDifference(string Path, string OldValue, string NewValue);

public static class AuditValueFormatter
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static string Format(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "（无）";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return value;
        }
    }

    public static IReadOnlyList<AuditFieldDifference> GetDifferences(string? oldValue, string? newValue)
    {
        if (!TryFlattenObject(oldValue, out var oldFields)
            || !TryFlattenObject(newValue, out var newFields))
        {
            return [];
        }

        return oldFields.Keys
            .Union(newFields.Keys, StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Where(path => !oldFields.TryGetValue(path, out var oldItem)
                           || !newFields.TryGetValue(path, out var newItem)
                           || !string.Equals(oldItem, newItem, StringComparison.Ordinal))
            .Select(path => new AuditFieldDifference(
                path,
                oldFields.GetValueOrDefault(path) ?? "（不存在）",
                newFields.GetValueOrDefault(path) ?? "（不存在）"))
            .ToArray();
    }

    private static bool TryFlattenObject(string? value, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            Flatten(document.RootElement, "", fields);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Flatten(JsonElement element, string prefix, IDictionary<string, string> fields)
    {
        var hasProperty = false;
        foreach (var property in element.EnumerateObject())
        {
            hasProperty = true;
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Flatten(property.Value, path, fields);
            }
            else
            {
                fields[path] = JsonSerializer.Serialize(property.Value);
            }
        }

        if (!hasProperty && prefix.Length > 0)
        {
            fields[prefix] = "{}";
        }
    }
}
