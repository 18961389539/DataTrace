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
        var tempFull = finalFull + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(finalFull)!);

        var raw = Serialize(payload);
        await using (var output = File.Create(tempFull))
        await using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            await brotli.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
        }

        var bytes = await File.ReadAllBytesAsync(tempFull, cancellationToken).ConfigureAwait(false);
        var crc = Crc32Util.Compute(bytes);
        File.Move(tempFull, finalFull, overwrite: true);
        return (relative.Replace('\\', '/'), bytes.LongLength, crc);
    }

    public async Task<CurvePayload> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var compressed = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
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
        var series = new List<CurveSeriesPayload>(seriesCount);
        for (var i = 0; i < seriesCount; i++)
        {
            var nameLen = reader.ReadUInt16();
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            var role = (SeriesRole)reader.ReadByte();
            var n = reader.ReadInt32();
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
