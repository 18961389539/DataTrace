using System.IO.Hashing;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Storage;

namespace DataTrace.Tests;

/// <summary>
/// 曲线文件 CRC 的写入契约（性能审查 P2）：CRC 改为随写随算之后，
/// 必须仍与"对落盘压缩字节做 Crc32.HashToUInt32"同值 —— 读回路径的
/// expectedCrc 校验（防损坏/防缺失混报）依赖这条一致性。
/// </summary>
public class CurveFileStoreCrcTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"curve-crc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CurvePayload Payload(int points = 257) => new()
    {
        PointCount = points,
        Series =
        [
            new CurveSeriesPayload { Name = "位移", Role = SeriesRole.X, Values = Enumerable.Range(0, points).Select(i => (float)i).ToArray() },
            new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = Enumerable.Range(0, points).Select(i => i * 0.5f).ToArray() }
        ]
    };

    [Fact]
    public async Task Written_crc_matches_static_hash_of_file_bytes()
    {
        var store = new CurveFileStore(_root);
        var (relative, size, crc) = await store.WriteAsync(
            new DateTime(2026, 10, 11, 8, 0, 0), "S20261011-0001", 7, 1, "ST007_PD", Payload());

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(size, bytes.LongLength);
        Assert.Equal(Crc32.HashToUInt32(bytes), crc);
    }

    [Fact]
    public async Task Read_validates_written_crc_and_roundtrips_payload()
    {
        var store = new CurveFileStore(_root);
        var payload = Payload();
        var (relative, _, crc) = await store.WriteAsync(
            new DateTime(2026, 10, 11, 8, 0, 0), "S20261011-0002", 8, 2, "ST008_PD", payload);

        var loaded = await store.ReadAsync(relative, crc);
        Assert.Equal(payload.PointCount, loaded.PointCount);
        Assert.Equal(2, loaded.Series.Count);
        Assert.Equal(payload.Series[1].Values, loaded.Series[1].Values);

        // 入库值与磁盘内容不一致时必须报损坏（与"文件缺失"区分开）。
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(relative, crc + 1));
    }
}
