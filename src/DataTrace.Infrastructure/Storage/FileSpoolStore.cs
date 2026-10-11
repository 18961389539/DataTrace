using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataTrace.Application.Runtime;
using DataTrace.Infrastructure.Persistence;

namespace DataTrace.Infrastructure.Storage;

public sealed class FileSpoolStore : ISpoolStore
{
    private const string PayloadSuffix = ".spool.json";
    private const string SidecarSuffix = ".spool.json.err.json";
    public const long DefaultMaxBacklogBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxPayloadBytes = 256L * 1024 * 1024;

    private readonly string _root;
    private readonly long _maxBacklogBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false
    };

    public FileSpoolStore(string root, long maxBacklogBytes = DefaultMaxBacklogBytes)
    {
        if (maxBacklogBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBacklogBytes));
        _root = root;
        _maxBacklogBytes = maxBacklogBytes;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SaveAsync(CollectSaveRequest request, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var name = $"{DateTime.Now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{PayloadSuffix}";
        var path = Path.Combine(_root, name);
        var temporaryPath = path + ".tmp";
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var bounded = new BoundedWriteStream(stream, MaxPayloadBytes);
                await JsonSerializer.SerializeAsync(bounded, request, JsonOptions, cancellationToken).ConfigureAwait(false);
                await bounded.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            var payloadLength = new FileInfo(temporaryPath).Length;
            if (payloadLength > MaxPayloadBytes)
            {
                throw new IOException($"Spool request exceeds the {MaxPayloadBytes} byte per-entry limit");
            }

            var currentBytes = GetBacklogBytes();
            if (payloadLength > _maxBacklogBytes - currentBytes)
            {
                throw new IOException($"Spool backlog quota exceeded ({currentBytes} + {payloadLength} > {_maxBacklogBytes} bytes)");
            }

            File.Move(temporaryPath, path);
            return name;
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
        finally
        {
            _writeGate.Release();
        }
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

    public async Task<IReadOnlyList<(string FileName, CollectSaveRequest Request)>> ListPageAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(take), "Spool page size must be between 1 and 256");
        if (!Directory.Exists(_root)) return [];

        var result = new List<(string, CollectSaveRequest)>();
        foreach (var file in PayloadFiles().Skip(skip).Take(take))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = await ReadPayloadAsync(file, cancellationToken).ConfigureAwait(false);
            if (request is not null) result.Add((Path.GetFileName(file), request));
        }

        return result;
    }

    public async Task<bool> HasMonthEntriesAsync(string monthKey, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return false;
        foreach (var file in PayloadFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = await ReadPayloadAsync(file, cancellationToken).ConfigureAwait(false);
            // A corrupt/oversized entry has unknown references; keep all candidate data until it is repaired.
            if (request is null || request.Record is null || request.Curves is null
                || string.Equals(request.MonthKey, monthKey, StringComparison.Ordinal)
                || string.Equals(RelativePathMonthKey(request.Record.ArchivePath), monthKey, StringComparison.Ordinal)
                || request.Curves.Any(curve => string.Equals(RelativePathMonthKey(curve.Record.RelativePath), monthKey, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    public async Task<bool> HasPendingForStationAsync(int stationId, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_root)) return false;
        foreach (var file in PayloadFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, useAsync: true);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (json.RootElement.TryGetProperty("Record", out var record)
                    && record.TryGetProperty("StationId", out var id)
                    && id.TryGetInt32(out var value) && value == stationId)
                    return true;
            }
            catch (JsonException) { return true; }
            catch (IOException) { return true; }
        }
        return false;
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

    private long GetBacklogBytes()
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*" + PayloadSuffix).Where(IsPayloadFile))
        {
            try
            {
                total = checked(total + new FileInfo(file).Length);
            }
            catch (FileNotFoundException)
            {
                // A concurrent replay may remove the entry while quota is being measured.
            }
            catch (DirectoryNotFoundException)
            {
                return total;
            }
        }

        return total;
    }

    /// <summary>旁车以 .err.json 结尾，不该被当成待补传的件。</summary>
    private static bool IsPayloadFile(string path)
        => !path.EndsWith(SidecarSuffix, StringComparison.OrdinalIgnoreCase);

    private static string? RelativePathMonthKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0].Length != 4 || parts[1].Length != 2
            || !parts[0].All(char.IsAsciiDigit) || !parts[1].All(char.IsAsciiDigit)) return null;
        var month = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return month is >= 1 and <= 12 ? parts[0] + parts[1] : null;
    }

    /// <summary>Enforces the serialized entry cap during writes, before a huge temporary file can fill the disk.</summary>
    private sealed class BoundedWriteStream(Stream inner, long limit) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            _written += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            inner.Write(buffer);
            _written += buffer.Length;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureCapacity(count);
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            _written += count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _written += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            // The owning FileStream is disposed by the caller after its durable flush.
            base.Dispose(disposing);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || _written > limit - count)
                throw new IOException($"Spool request exceeds the {limit} byte per-entry limit");
        }
    }

    /// <summary>Bounds actual bytes consumed so replacing/growing a file after the length check cannot bypass the cap.</summary>
    private sealed class BoundedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            EnsureWithinLimit(read);
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            EnsureWithinLimit(read);
            return read;
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            EnsureWithinLimit(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            EnsureWithinLimit(read);
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            // The owning FileStream is disposed by the caller.
            base.Dispose(disposing);
        }
        private void EnsureWithinLimit(int count)
        {
            if (_read > limit - count)
                throw new IOException($"Spool request exceeds the {limit} byte per-entry limit");
            _read += count;
        }
    }

    private static async Task<CollectSaveRequest?> ReadPayloadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path).Length > MaxPayloadBytes) return null;
            await using var file = File.OpenRead(path);
            using var stream = new BoundedReadStream(file, MaxPayloadBytes);
            var request = await JsonSerializer.DeserializeAsync<CollectSaveRequest>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return IsValidRequest(request) ? request : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static bool IsValidRequest(CollectSaveRequest? request)
        => request is not null
            && RuntimeDbFactory.IsValidMonthKey(request.MonthKey)
            && request.Record is not null
            && !string.IsNullOrWhiteSpace(request.Record.SerialNo)
            && !string.IsNullOrWhiteSpace(request.Record.PalletCode)
            && request.Curves is not null
            && request.Curves.All(curve => curve?.Record is not null
                && curve.Payload is not null
                && curve.Payload.Series is not null
                && curve.Payload.Series.All(series => series?.Values is not null));

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
