using System.Text.Json;
using DataTrace.Infrastructure.Backup;

namespace DataTrace.Web.Services;

/// <summary>
/// 把查询方案按账号落到 DataRoot/query-presets/。不进配置快照，改方案不会触发采集重载。
/// </summary>
public sealed class FileQueryPresetStore : IQueryPresetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _directory;
    private readonly object _gate = new();

    public FileQueryPresetStore(DataRootPaths paths)
    {
        _directory = Path.Combine(paths.Root, "query-presets");
        Directory.CreateDirectory(_directory);
    }

    public Task<IReadOnlyList<QueryPreset>> ListAsync(string account, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(account);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return Task.FromResult<IReadOnlyList<QueryPreset>>([]);
            }

            try
            {
                var json = File.ReadAllText(path);
                var stored = JsonSerializer.Deserialize<List<StoredPreset>>(json, JsonOptions) ?? [];
                var presets = stored
                    .Where(item => item is not null
                        && !string.IsNullOrWhiteSpace(item.Name)
                        && !string.IsNullOrWhiteSpace(item.Url)
                        && !string.IsNullOrWhiteSpace(item.Range))
                    .Select(item => new QueryPreset(item.Name.Trim(), item.Url.Trim(), item.Range.Trim()))
                    .Where(preset => RecordListNavigation.IsListUrl(preset.Url)
                        && QueryPreset.Ranges.Any(range => range.Key == preset.Range))
                    .DistinctBy(preset => preset.Name)
                    .Take(30)
                    .ToList();
                return Task.FromResult<IReadOnlyList<QueryPreset>>(presets);
            }
            catch (JsonException)
            {
                return Task.FromResult<IReadOnlyList<QueryPreset>>([]);
            }
        }
    }

    public Task SaveAllAsync(string account, IReadOnlyList<QueryPreset> presets, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = PathFor(account);
        var payload = presets
            .Where(preset => preset is not null
                && !string.IsNullOrWhiteSpace(preset.Name)
                && RecordListNavigation.IsListUrl(preset.Url)
                && QueryPreset.Ranges.Any(range => range.Key == preset.Range))
            .DistinctBy(preset => preset.Name)
            .Take(30)
            .Select(preset => new StoredPreset(preset.Name, preset.Url, preset.Range))
            .ToList();

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var temp = path + ".tmp";
        lock (_gate)
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string account)
    {
        var key = SanitizeAccount(account);
        return Path.Combine(_directory, key + ".json");
    }

    /// <summary>账号可能含邮箱特殊字符；文件名只留安全字符，避免路径穿越。</summary>
    internal static string SanitizeAccount(string account)
    {
        var raw = (account ?? "").Trim();
        if (raw.Length == 0)
        {
            throw new ArgumentException("账号不能为空", nameof(account));
        }

        var chars = new char[Math.Min(raw.Length, 80)];
        var n = 0;
        foreach (var ch in raw)
        {
            if (n >= chars.Length) break;
            chars[n++] = char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@'
                ? ch
                : '_';
        }

        var safe = new string(chars, 0, n);
        return string.IsNullOrWhiteSpace(safe) ? "account" : safe;
    }

    private sealed record StoredPreset(string Name, string Url, string Range);
}
