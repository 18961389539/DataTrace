using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataTrace.Application.Runtime;

namespace DataTrace.Infrastructure.Storage;

public sealed class FileSpoolStore : ISpoolStore
{
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

    public async Task SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var name = $"{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.spool.json";
        var path = Path.Combine(_root, name);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, request, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var result = new List<(string, CollectSaveRequest)>();
        foreach (var file in Directory.GetFiles(_root, "*.spool.json").OrderBy(x => x))
        {
            await using var stream = File.OpenRead(file);
            var request = await JsonSerializer.DeserializeAsync<CollectSaveRequest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (request is not null)
            {
                result.Add((Path.GetFileName(file), request));
            }
        }

        return result;
    }

    public Task<SpoolBacklog> DescribeAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root))
        {
            return Task.FromResult(new SpoolBacklog(0, null));
        }

        var count = 0;
        DateTime? oldest = null;
        foreach (var file in Directory.GetFiles(_root, "*.spool.json"))
        {
            count++;
            var at = CreatedAt(file);
            if (oldest is null || at < oldest)
            {
                oldest = at;
            }
        }

        return Task.FromResult(new SpoolBacklog(count, oldest));
    }

    public Task DeleteAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, fileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

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
}
