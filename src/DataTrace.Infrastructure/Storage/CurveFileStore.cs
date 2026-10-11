using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Storage;

public sealed class CurveFileStore : ICurveFileStore
{
    private readonly string _root;
    private const byte Version = 1;

    /// <summary>单条序列的点数上限。仅用于挡住损坏文件里的非法长度，远高于任何真实配置。</summary>
    private const int MaxPointsPerSeries = 1_000_000;

    /// <summary>单条曲线的序列数上限，同为防御性上限。</summary>
    private const int MaxSeriesPerCurve = 64;
    private const int MaxCompressedBytes = 32 * 1024 * 1024;
    private const int MaxExpandedBytes = 64 * 1024 * 1024;

    private static readonly byte[] Magic = "DTCR"u8.ToArray();

    public CurveFileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public Task<(string RelativePath, long FileSize, uint Crc32)> WriteAsync(
        DateTime triggerTime,
        string serialNo,
        int stationId,
        int positionIndex,
        string curveCode,
        CurvePayload payload,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relative = Path.Combine(
            triggerTime.ToString("yyyy"),
            triggerTime.ToString("MM"),
            triggerTime.ToString("dd"),
            $"{Sanitize(serialNo)}_{stationId}_{positionIndex}_{Sanitize(curveCode)}.curve");
        var finalFull = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(finalFull)!);

        // 绝不覆盖已存在的文件：文件名里没有记录的唯一标识，序列号重复时
        // （配置库回滚/恢复后计数器重发同一号，或人工改库）两条记录会争同一个路径。
        // 覆盖之后再回滚，删掉的就是上一条记录的波形 —— 而它已经不在事务里，救不回来。
        // 撞名就让开一格，本次写的永远是自己那份。
        finalFull = AvailablePath(finalFull);
        var tempFull = finalFull + ".tmp";

        var crc = WriteCompressed(tempFull, payload);
        var size = new FileInfo(tempFull).Length;
        File.Move(tempFull, finalFull, overwrite: true);
        return Task.FromResult((Path.GetRelativePath(_root, finalFull).Replace('\\', '/'), size, crc));
    }

    /// <summary>
    /// 序列化直写压缩流并随写算 CRC。不再先在内存里拼出完整 raw 数组
    /// （最坏 64MB 量级）再交给压缩 —— 中间只留压缩器自身的缓冲（性能审查 P2）。
    /// </summary>
    private static uint WriteCompressed(string tempFull, CurvePayload payload)
    {
        var crc32 = new Crc32();
        using var output = File.Create(tempFull);
        using var hashing = new HashingStream(output, crc32);
        using (var brotli = new BrotliStream(hashing, CompressionLevel.Fastest, leaveOpen: true))
        {
            using var writer = new BinaryWriter(brotli, Encoding.UTF8, leaveOpen: true);
            WritePayloadFields(writer, payload);
            writer.Flush();
        }

        // hashing 在 brotli 之后关闭：帧尾字节也要进 CRC。两者关闭顺序由 using 嵌套保证。
        return BinaryPrimitives.ReadUInt32LittleEndian(crc32.GetCurrentHash());
    }

    public async Task<CurvePayload> ReadAsync(string relativePath, uint? expectedCrc = null, CancellationToken cancellationToken = default)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(full);
        if (info.Length > MaxCompressedBytes)
            throw new InvalidDataException($"曲线文件超过压缩大小上限：{relativePath}");
        byte[] compressed;
        await using (var inputFile = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
        using (var compressedBuffer = new MemoryStream((int)Math.Min(info.Length, MaxCompressedBytes)))
        {
            var compressedReadChunk = new byte[81920];
            while (true)
            {
                var read = await inputFile.ReadAsync(compressedReadChunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (compressedBuffer.Length + read > MaxCompressedBytes)
                    throw new InvalidDataException($"曲线文件超过压缩大小上限：{relativePath}");
                await compressedBuffer.WriteAsync(compressedReadChunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            compressed = compressedBuffer.ToArray();
        }

        // 校验的是压缩后的字节，与写入口径一致。损坏与缺失必须区分开：
        // 都报"文件缺失"的话，现场会去查备份而不是查磁盘。
        if (expectedCrc is { } expected && Crc32.HashToUInt32(compressed) != expected)
        {
            throw new InvalidDataException(
                $"曲线文件校验失败：CRC 与入库值不一致（{relativePath}）");
        }

        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await brotli.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > MaxExpandedBytes)
                throw new InvalidDataException($"曲线文件超过解压大小上限：{relativePath}");
            await output.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Deserialize(output.ToArray());
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
        {
            File.Delete(full);
        }

        return Task.CompletedTask;
    }

    public Task DeleteMonthAsync(string yyyy, string mm, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_root, yyyy, mm);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        return Task.CompletedTask;
    }

    public static byte[] Serialize(CurvePayload payload)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WritePayloadFields(writer, payload);
        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>字段布局的唯一写入口：文件直写与内存 Serialize 共用，两端口径永不漂移。</summary>
    private static void WritePayloadFields(BinaryWriter writer, CurvePayload payload)
    {
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(payload.PointCount);
        writer.Write(payload.Series.Count);
        foreach (var series in payload.Series)
        {
            var nameBytes = Encoding.UTF8.GetBytes(series.Name);
            writer.Write((ushort)nameBytes.Length);
            writer.Write(nameBytes);
            writer.Write((byte)series.Role);
            writer.Write(series.Values.Length);
            foreach (var v in series.Values)
            {
                writer.Write(v);
            }
        }
    }

    public static CurvePayload Deserialize(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8);
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
        {
            throw new InvalidDataException("曲线文件头损坏");
        }

        var version = reader.ReadByte();
        if (version != Version)
        {
            throw new InvalidDataException($"不支持的曲线版本 {version}");
        }

        var pointCount = reader.ReadInt32();
        var seriesCount = reader.ReadInt32();
        if (pointCount < 0 || pointCount > MaxPointsPerSeries)
        {
            throw new InvalidDataException($"曲线点数非法：{pointCount}");
        }

        if (seriesCount < 0 || seriesCount > MaxSeriesPerCurve)
        {
            throw new InvalidDataException($"曲线序列数非法：{seriesCount}");
        }

        var series = new List<CurveSeriesPayload>(seriesCount);
        for (var i = 0; i < seriesCount; i++)
        {
            var nameLen = reader.ReadUInt16();
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            var role = (SeriesRole)reader.ReadByte();
            var n = reader.ReadInt32();
            // 长度先校验再分配：损坏文件里的长度字段是任意的，
            // 直接 new float[n] 会变成一次大内存分配（或直接抛 OverflowException）。
            if (n < 0 || n > MaxPointsPerSeries)
            {
                throw new InvalidDataException($"曲线序列点数非法：{n}");
            }

            var values = new float[n];
            for (var j = 0; j < n; j++)
            {
                values[j] = reader.ReadSingle();
            }

            series.Add(new CurveSeriesPayload { Name = name, Role = role, Values = values });
        }

        return new CurvePayload { PointCount = pointCount, Series = series };
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    /// <summary>路径被占用时依次尝试 <c>-2</c>、<c>-3</c>… 后缀，返回一个当前不存在的路径。</summary>
    private static string AvailablePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 把写过的每个字节喂给非加密哈希的转发流。与 Crc32.HashToUInt32（对整个文件内容）同值：
    /// 字节序按 little-endian 取回，读回路径的 expectedCrc 校验就是这条一致性的验收口。
    /// </summary>
    private sealed class HashingStream(Stream inner, NonCryptographicHashAlgorithm hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hash.Append(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.Append(buffer);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.Append(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }
}
