using System.Buffers.Binary;
using System.IO.Compression;
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

    private static readonly byte[] Magic = "DTCR"u8.ToArray();

    public CurveFileStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public async Task<(string RelativePath, long FileSize, uint Crc32)> WriteAsync(
        DateTime triggerTime,
        string serialNo,
        int stationId,
        int positionIndex,
        string curveCode,
        CurvePayload payload,
        CancellationToken cancellationToken = default)
    {
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

        var raw = Serialize(payload);
        await using (var output = File.Create(tempFull))
        await using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            await brotli.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
        }

        var bytes = await File.ReadAllBytesAsync(tempFull, cancellationToken).ConfigureAwait(false);
        var crc = Crc32Util.Compute(bytes);
        File.Move(tempFull, finalFull, overwrite: true);
        return (Path.GetRelativePath(_root, finalFull).Replace('\\', '/'), bytes.LongLength, crc);
    }

    public async Task<CurvePayload> ReadAsync(string relativePath, uint? expectedCrc = null, CancellationToken cancellationToken = default)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var compressed = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);

        // 校验的是压缩后的字节，与写入口径一致。损坏与缺失必须区分开：
        // 都报"文件缺失"的话，现场会去查备份而不是查磁盘。
        if (expectedCrc is { } expected && Crc32Util.Compute(compressed) != expected)
        {
            throw new InvalidDataException(
                $"曲线文件校验失败：CRC 与入库值不一致（{relativePath}）");
        }

        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await brotli.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
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

        return ms.ToArray();
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
}

internal static class Crc32Util
{
    private static readonly uint[] Table = Create();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] Create()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
