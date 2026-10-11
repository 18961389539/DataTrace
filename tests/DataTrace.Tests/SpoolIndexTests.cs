using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Storage;

namespace DataTrace.Tests;

/// <summary>
/// 补传暂存"工位→待处理位置"轻量索引的行为闭环（性能审查 P1）：
/// 保存/删除即时反映、重启经旁车索引恢复、损坏文件对所有工站/月库一律报"有积压"。
/// 索引只准是快路径，不准改变 fail-safe 语义 —— 漏报待处理任务等于放行重复采集。
/// </summary>
public class SpoolIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"spool-index-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CollectSaveRequest ValidRequest(string monthKey = "202609", int stationId = 7) => new()
    {
        MonthKey = monthKey,
        Record = new CollectRecord
        {
            PalletCode = "P0001",
            SerialNo = $"S-{stationId}-{Guid.NewGuid():N}".Substring(0, 20),
            StationId = stationId,
            StationCode = $"ST{stationId:D3}",
            TriggerTime = new DateTime(2026, 9, 20, 8, 0, 0)
        },
        Curves = []
    };

    [Fact]
    public async Task HasPendingForStation_reflects_save_and_delete_immediately()
    {
        var store = new FileSpoolStore(_root);
        var name = await store.SaveAsync(ValidRequest());

        Assert.True(await store.HasPendingForStationAsync(7));
        Assert.False(await store.HasPendingForStationAsync(8));

        await store.DeleteAsync(name);
        Assert.False(await store.HasPendingForStationAsync(7));
    }

    [Fact]
    public async Task Index_survives_restart_via_sidecar_without_content_parse()
    {
        var first = new FileSpoolStore(_root);
        await first.SaveAsync(ValidRequest(stationId: 7));
        Assert.True(File.Exists(Path.Combine(_root, "_meta", "spool.index.json")));

        // 新实例模拟重启：旁车指纹吻合时不需要解析任何内容就能回答。
        var second = new FileSpoolStore(_root);
        Assert.True(await second.HasPendingForStationAsync(7));
        Assert.False(await second.HasPendingForStationAsync(8));
        Assert.True(await second.HasMonthEntriesAsync("202609"));
        Assert.False(await second.HasMonthEntriesAsync("202610"));
    }

    [Fact]
    public async Task Corrupt_payload_blocks_every_station_and_month()
    {
        var store = new FileSpoolStore(_root);
        await store.SaveAsync(ValidRequest());
        await File.WriteAllTextAsync(
            Path.Combine(_root, "20260101000000000_broken.spool.json"),
            "{ this is not json");

        // 新实例从旁车/目录对账：损坏文件解析不了，任何工站与月库都必须按"有积压"回答。
        var second = new FileSpoolStore(_root);
        Assert.True(await second.HasPendingForStationAsync(7));
        Assert.True(await second.HasPendingForStationAsync(99));
        Assert.True(await second.HasMonthEntriesAsync("202610"));
    }

    [Fact]
    public async Task HasMonthEntries_matches_curve_archive_month_not_only_record_month()
    {
        var request = new CollectSaveRequest
        {
            MonthKey = "202608",
            Record = new CollectRecord
            {
                PalletCode = "P0001",
                SerialNo = "S-curve-month",
                StationId = 3,
                StationCode = "ST003",
                TriggerTime = new DateTime(2026, 8, 20, 8, 0, 0)
            },
            Curves =
            [
                new CurvePayloadWrite
                {
                    Record = new CurveRecord
                    {
                        CurveDefinitionId = 1,
                        CurveCode = "ST003_PD",
                        CurveName = "曲线",
                        PositionIndex = 1,
                        PointCount = 1,
                        // 曲线文件归档在 9 月目录（路径形状 yyyy/MM/...）：记录本身在 8 月库里。
                        RelativePath = "2026/09/curves/sample.bin"
                    },
                    Payload = new CurvePayload
                    {
                        PointCount = 1,
                        Series = [new CurveSeriesPayload { Name = "压力", Role = SeriesRole.Y, Values = [1f] }]
                    }
                }
            ]
        };

        var store = new FileSpoolStore(_root);
        await store.SaveAsync(request);

        Assert.True(await store.HasMonthEntriesAsync("202608"));
        Assert.True(await store.HasMonthEntriesAsync("202609"));
        Assert.False(await store.HasMonthEntriesAsync("202610"));
    }

    [Fact]
    public async Task Externally_added_file_is_picked_up_by_reconcile()
    {
        var store = new FileSpoolStore(_root);
        await store.SaveAsync(ValidRequest(stationId: 7));
        Assert.False(await store.HasPendingForStationAsync(8));

        // 进程外拷进来的文件（或崩溃窗口内 Move 之后、索引更新之前）：目录里多了索引不知道的文件，
        // 对账必须把它解析进来，而不是按旧索引直接回答。
        var payload = System.Text.Json.JsonSerializer.Serialize(
            ValidRequest(stationId: 8),
            new System.Text.Json.JsonSerializerOptions());
        await File.WriteAllTextAsync(Path.Combine(_root, "20260101000000000_external.spool.json"), payload);

        Assert.True(await store.HasPendingForStationAsync(8));
    }
}
