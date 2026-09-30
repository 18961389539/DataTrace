using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataTrace.Application.Runtime;

namespace DataTrace.Infrastructure.Storage;

public sealed class FileSpoolStore : ISpoolStore
{
    private const string PayloadSuffix = ".spool.json";
    private const string SidecarSuffix = ".spool.json.err.json";

    private readonly string _root;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false
    };

    public FileSpoolStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var name = $"{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{PayloadSuffix}";
        var path = Path.Combine(_root, name);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, request, JsonOptions, cancellationToken).ConfigureAwait(false);
        return name;
    }

    public async Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var result = new List<(string, CollectSaveRequest)>();
        foreach (var file in PayloadFiles())
        {
            var request = await ReadPayloadAsync(file, cancellationToken).ConfigureAwait(false);
            if (request is not null)
            {
                result.Add((Path.GetFileName(file), request));
            }
        }

        return result;
    }

    /// <summary>按文件名读一条：手动补传单条用它，不给整队做无谓的反序列化。</summary>
    public async Task<CollectSaveRequest?> ReadOneAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, fileName);
        return File.Exists(path)
            ? await ReadPayloadAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task<IReadOnlyList<SpoolEntry>> ListEntriesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var entries = new List<SpoolEntry>();
        foreach (var file in PayloadFiles())
        {
            var name = Path.GetFileName(file);
            var request = await ReadPayloadAsync(file, cancellationToken).ConfigureAwait(false);
            var failure = await ReadFailureAsync(name, cancellationToken).ConfigureAwait(false);

            // 反序列化失败的文件不跳过：它本身就是一条"这里有问题"的线索，
            // 从诊断页消失只会让人以为补传队列已经空了。
            var record = request?.Record;
            entries.Add(new SpoolEntry(
                name,
                CreatedAt(file),
                request?.MonthKey ?? "",
                record?.StationCode ?? "",
                record?.PalletCode ?? "",
                record?.SerialNo ?? "",
                record?.TriggerTime ?? CreatedAt(file),
                record?.ResultCode ?? 0,
                failure?.Attempts ?? 0,
                failure?.LastAttemptAt,
                failure?.Error ?? (request is null ? "缓存文件无法解析" : null)));
        }

        return entries;
    }

    public Task<SpoolBacklog> DescribeAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return Task.FromResult(new SpoolBacklog(0, null));
        }

        // 只数件数与最早一条，不排序：看板、报警与诊断页都在按秒轮询这一条，
        // 而它们要的只是两个数 —— 顺序留给要逐条列的 ListAsync / ListEntriesAsync。
        var count = 0;
        DateTime? oldest = null;
        foreach (var path in Directory.EnumerateFiles(_root, "*" + PayloadSuffix))
        {
            if (!IsPayloadFile(path))
            {
                continue;
            }

            count++;
            var at = CreatedAt(path);
            if (oldest is null || at < oldest)
            {
                oldest = at;
            }
        }

        return Task.FromResult(new SpoolBacklog(count, oldest));
    }

    public async Task NoteFailureAsync(string fileName, string error, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var previous = await ReadFailureAsync(fileName, cancellationToken).ConfigureAwait(false);
        var next = new SpoolFailure(
            (previous?.Attempts ?? 0) + 1,
            DateTime.Now,
            Truncate(error));

        var path = Path.Combine(_root, fileName + ".err.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, next, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        // 旁车跟着主文件一起走：留下孤儿旁车会让下一轮 DescribeAsync 只数到主文件、
        // 而诊断页永远等不到那次的失败原因。
        var sidecar = Path.Combine(_root, fileName + ".err.json");
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }

        return Task.CompletedTask;
    }

    private IEnumerable<string> PayloadFiles()
        => Directory.GetFiles(_root, "*" + PayloadSuffix)
            .Where(IsPayloadFile)
            .OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>旁车以 .err.json 结尾，不该被当成待补传的件。</summary>
    private static bool IsPayloadFile(string path)
        => !path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase);

    private static async Task<CollectSaveRequest?> ReadPayloadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CollectSaveRequest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task<SpoolFailure?> ReadFailureAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, fileName + ".err.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SpoolFailure>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>失败原因进旁车文件，不设长度上限会让一条巨型异常消息把文件读写拖成负担。</summary>
    private static string Truncate(string error)
        => error.Length <= 500 ? error : error[..500];

    private static DateTime CreatedAt(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Length >= 17
            && DateTime.TryParseExact(
                name.AsSpan(0, 17),
                "yyyyMMddHHmmssfff",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        return File.GetLastWriteTime(path);
    }

    /// <summary>旁车文件：这条缓存补传失败了几次、上次是什么时候、为什么。</summary>
    private sealed record SpoolFailure(int Attempts, DateTime LastAttemptAt, string Error);
}
