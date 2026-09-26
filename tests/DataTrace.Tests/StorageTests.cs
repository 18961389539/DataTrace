using System.Text;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Storage;

namespace DataTrace.Tests;

/// <summary>曲线文件：二进制格式自洽、原子落盘、CRC 稳定、整月清除。</summary>
public class CurveFileStoreTests
{
    private static CurvePayload Payload(int points = 3) => new()
    {
        PointCount = points,
        Series =
        [
            new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [1.5f, 2.5f, 3.5f] },
            new CurveSeriesPayload { Name = "位移", Role = SeriesRole.X, Values = [0f, 1f, 2f] }
        ]
    };

    private static readonly DateTime TriggerTime = new(2026, 9, 19, 10, 30, 0, DateTimeKind.Local);

    [Fact]
    public void Serialize_and_deserialize_roundtrip_preserves_everything()
    {
        var payload = Payload();
        var restored = CurveFileStore.Deserialize(CurveFileStore.Serialize(payload));

        Assert.Equal(payload.PointCount, restored.PointCount);
        Assert.Equal(2, restored.Series.Count);
        Assert.Equal("压力", restored.Series[0].Name);
        Assert.Equal(SeriesRole.Y, restored.Series[0].Role);
        Assert.Equal(payload.Series[0].Values, restored.Series[0].Values);
        Assert.Equal("位移", restored.Series[1].Name);
        Assert.Equal(SeriesRole.X, restored.Series[1].Role);
    }

    [Fact]
    public void Serialize_handles_empty_series_and_zero_points()
    {
        var payload = new CurvePayload { PointCount = 0, Series = [] };
        var restored = CurveFileStore.Deserialize(CurveFileStore.Serialize(payload));

        Assert.Equal(0, restored.PointCount);
        Assert.Empty(restored.Series);
    }

    [Fact]
    public void Deserialize_rejects_bad_magic()
    {
        var bytes = Encoding.ASCII.GetBytes("XXXX");
        var ex = Assert.Throws<InvalidDataException>(() => CurveFileStore.Deserialize([.. bytes, 1, 0, 0, 0, 0]));
        Assert.Contains("文件头损坏", ex.Message);
    }

    [Fact]
    public void Deserialize_rejects_unsupported_version()
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("DTCR"u8.ToArray());
            writer.Write((byte)99);
        }

        var ex = Assert.Throws<InvalidDataException>(() => CurveFileStore.Deserialize(ms.ToArray()));
        Assert.Contains("99", ex.Message);
    }

    [Fact]
    public void Deserialize_throws_on_truncated_body()
    {
        using var ms = new MemoryStream();
        using (var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("DTCR"u8.ToArray());
            writer.Write((byte)1);
            writer.Write(50); // 声明 50 点，后文缺失
        }

        Assert.ThrowsAny<Exception>(() => CurveFileStore.Deserialize(ms.ToArray()));
    }

    [Fact]
    public async Task Write_creates_dated_file_and_returns_metadata()
    {
        using var workspace = new TempWorkspace();
        var store = new CurveFileStore(workspace.Path("curves"));

        var (relative, size, crc) = await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", Payload());

        Assert.StartsWith("2026/09/19/", relative);
        Assert.EndsWith(".curve", relative);
        Assert.Equal(4, relative.Split('/').Length);
        Assert.Contains("P0001", relative);

        var full = Path.Combine(workspace.Path("curves"), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full));
        Assert.Equal(new FileInfo(full).Length, size);
        Assert.True(size > 0);
        Assert.NotEqual(0u, crc);
    }

    [Fact]
    public async Task Write_then_read_returns_same_payload()
    {
        using var workspace = new TempWorkspace();
        var store = new CurveFileStore(workspace.Path("curves"));
        var payload = Payload();

        var (relative, _, _) = await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", payload);
        var restored = await store.ReadAsync(relative);

        Assert.Equal(payload.PointCount, restored.PointCount);
        Assert.Equal(payload.Series[0].Values, restored.Series[0].Values);
        Assert.Equal(payload.Series[1].Values, restored.Series[1].Values);
    }

    [Fact]
    public async Task Crc_is_stable_for_same_payload_and_changes_with_content()
    {
        using var workspace = new TempWorkspace();
        var store = new CurveFileStore(workspace.Path("curves"));

        var first = await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", Payload());
        var again = await store.WriteAsync(TriggerTime, "P0002", 3, 1, "ST030_PD", Payload());
        Assert.Equal(first.Crc32, again.Crc32);

        var changed = new CurvePayload
        {
            PointCount = 3,
            Series = [new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [9f, 9f, 9f] }]
        };
        var third = await store.WriteAsync(TriggerTime, "P0003", 3, 1, "ST030_PD", changed);
        Assert.NotEqual(first.Crc32, third.Crc32);
    }

    [Fact]
    public async Task Rewriting_same_key_never_clobbers_the_previous_file()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("curves");
        var store = new CurveFileStore(root);

        var first = await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", Payload());
        var second = await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", Payload(5));

        Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        Assert.NotEqual(first.RelativePath, second.RelativePath);

        // 同名路径上的第一份必须还在：文件名里没有记录唯一标识，序列号重复时
        // 两条记录会争同一个路径；覆盖之后再因入库失败回滚，删掉的就是上一条记录的波形。
        var restoredFirst = await store.ReadAsync(first.RelativePath);
        Assert.Equal(3, restoredFirst.PointCount);
        var restoredSecond = await store.ReadAsync(second.RelativePath);
        Assert.Equal(5, restoredSecond.PointCount);
    }

    /// <summary>读取时校验 CRC：内容被动过要报"损坏"，而不是当成"文件不存在"。</summary>
    [Fact]
    public async Task Read_detects_corruption_via_crc()
    {
        using var workspace = new TempWorkspace();
        var store = new CurveFileStore(workspace.Path("curves"));

        var (relative, _, crc) = await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", Payload());

        // 校验值一致时正常读回。
        Assert.Equal(3, (await store.ReadAsync(relative, crc)).PointCount);

        // 改动内容（且保持可解压）后 CRC 必然对不上。
        var full = Path.Combine(workspace.Path("curves"), relative.Replace('/', Path.DirectorySeparatorChar));
        var bytes = await File.ReadAllBytesAsync(full);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(full, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(relative, crc));
        // 不传校验值时按老行为尽力读（历史行没有校验值）。
        Assert.Equal(3, (await store.ReadAsync(relative)).PointCount);
    }

    /// <summary>损坏文件里的长度字段是任意的，解序列化前必须挡住，不能让一次大内存分配打穿进程。</summary>
    [Fact]
    public void Deserialize_rejects_absurd_lengths_instead_of_allocating()
    {
        var header = new byte[13];
        "DTCR"u8.ToArray().CopyTo(header, 0);
        header[4] = 1;                                   // 版本
        BitConverter.GetBytes(3).CopyTo(header, 5);      // 点数
        BitConverter.GetBytes(int.MaxValue).CopyTo(header, 9);  // 序列数：任意值

        var ex = Assert.Throws<InvalidDataException>(() => CurveFileStore.Deserialize(header));
        Assert.Contains("序列数非法", ex.Message);
    }

    [Fact]
    public async Task Invalid_file_name_characters_are_sanitized()
    {
        using var workspace = new TempWorkspace();
        var store = new CurveFileStore(workspace.Path("curves"));

        var (relative, _, _) = await store.WriteAsync(TriggerTime, "P/000\\1", 3, 1, "ST:030?PD", Payload());

        // 非法字符被替换，防止串到其他目录或产生无扩展名文件。
        Assert.Equal(4, relative.Split('/').Length);
        Assert.DoesNotContain(":", relative);
        Assert.DoesNotContain("\\", relative);
        Assert.EndsWith(".curve", relative);
    }

    [Fact]
    public async Task Delete_month_removes_directory_and_is_idempotent()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("curves");
        var store = new CurveFileStore(root);

        await store.WriteAsync(TriggerTime, "P0001", 3, 1, "ST030_PD", Payload());
        Assert.True(Directory.Exists(Path.Combine(root, "2026", "09")));

        await store.DeleteMonthAsync("2026", "09");
        Assert.False(Directory.Exists(Path.Combine(root, "2026", "09")));

        // 重复删除或删除不存在月份不应抛异常，保留策略任务要能长期空转。
        await store.DeleteMonthAsync("2026", "09");
        await store.DeleteMonthAsync("1999", "01");
    }
}

/// <summary>写库失败补传缓存：落盘、可枚举、可删除。</summary>
public class FileSpoolStoreTests
{
    private static CollectSaveRequest BuildRequest(string palletCode, string serialNo, string monthKey = "202609", bool withCurve = false)
    {
        var trigger = new DateTime(2026, 9, 19, 11, 0, 0, DateTimeKind.Local);
        return new CollectSaveRequest
        {
            MonthKey = monthKey,
            Record = new CollectRecord
            {
                SerialNo = serialNo,
                PalletCode = palletCode,
                StationId = 30,
                StationCode = "ST030",
                TriggerTime = trigger,
                CompleteTime = trigger.AddMilliseconds(150),
                DurationMs = 150,
                ResultCode = ResultCodes.Success,
                Judgement = Judgement.Ok,
                Products = [new ProductRecord { PositionIndex = 1, Occupied = true, Judgement = Judgement.Ok }],
                TagValues =
                [
                    new TagValue { TagId = 1, TagName = "压力", PositionIndex = 1, DataType = PlcDataType.Float, NumericValue = 12.5 }
                ]
            },
            Curves = withCurve
                ?
                [
                    new CurvePayloadWrite
                    {
                        Record = new CurveRecord { CurveDefinitionId = 1, CurveCode = "ST030_PD", CurveName = "位移压力曲线", PositionIndex = 1, PointCount = 2 },
                        Payload = new CurvePayload
                        {
                            PointCount = 2,
                            Series = [new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [1f, 2f] }]
                        }
                    }
                ]
                : []
        };
    }

    [Fact]
    public async Task Empty_directory_lists_nothing()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Missing_directory_lists_nothing()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));
        Directory.Delete(workspace.Path("spool"), recursive: true);

        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task Save_then_list_roundtrips_request()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("spool");
        var store = new FileSpoolStore(root);

        await store.SaveAsync(BuildRequest("P0001", "20260919-000001"));

        Assert.Single(Directory.GetFiles(root, "*.spool.json"));
        var items = await store.ListAsync();
        var (file, request) = Assert.Single(items);
        Assert.EndsWith(".spool.json", file);
        Assert.Equal("202609", request.MonthKey);
        Assert.Equal("P0001", request.Record.PalletCode);
        Assert.Equal("20260919-000001", request.Record.SerialNo);
        Assert.Equal(ResultCodes.Success, request.Record.ResultCode);
        Assert.Single(request.Record.Products);
        Assert.Equal(12.5, request.Record.TagValues.Single().NumericValue);
    }

    [Fact]
    public async Task Curves_survive_the_roundtrip()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));

        await store.SaveAsync(BuildRequest("P0001", "20260919-000001", withCurve: true));

        var (_, request) = Assert.Single(await store.ListAsync());
        var curve = Assert.Single(request.Curves);
        Assert.Equal("ST030_PD", curve.Record.CurveCode);
        Assert.Equal(2, curve.Payload.PointCount);
        Assert.Equal(new[] { 1f, 2f }, curve.Payload.Series.Single().Values);
    }

    [Fact]
    public async Task Multiple_entries_are_kept_separately()
    {
        using var workspace = new TempWorkspace();
        var store = new FileSpoolStore(workspace.Path("spool"));

        await store.SaveAsync(BuildRequest("P0001", "S1"));
        await store.SaveAsync(BuildRequest("P0002", "S2"));
        await store.SaveAsync(BuildRequest("P0003", "S3", monthKey: "202610"));

        var items = await store.ListAsync();
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { "P0001", "P0002", "P0003" }, items.Select(i => i.Request.Record.PalletCode).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Delete_removes_single_entry_and_ignores_unknown_name()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("spool");
        var store = new FileSpoolStore(root);

        await store.SaveAsync(BuildRequest("P0001", "S1"));
        await store.SaveAsync(BuildRequest("P0002", "S2"));

        var items = await store.ListAsync();
        await store.DeleteAsync(items[0].FileName);
        Assert.Single(await store.ListAsync());

        await store.DeleteAsync("not-exists.spool.json");
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task Corrupted_entry_surfaces_error_from_list()
    {
        using var workspace = new TempWorkspace();
        var root = workspace.Path("spool");
        var store = new FileSpoolStore(root);

        await store.SaveAsync(BuildRequest("P0001", "S1"));
        await File.WriteAllTextAsync(Path.Combine(root, "broken.spool.json"), "{ not json");

        // 现状：坏文件会让整个列表枚举失败（补传循环会反复重试同一批文件）。
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => store.ListAsync());
    }
}
