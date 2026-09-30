using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataTrace.Tests;

/// <summary>
/// 工站试读：读得对，且什么都不改。
/// </summary>
/// <remarks>
/// "什么都不改"里有一大半是结构性保证 —— 试读的构造依赖里没有任何写入口
/// （没有 ICollectWriter、ISpoolStore、IActiveSessionStore、ICollectArchiveStore、IRuntimeStatusHub），
/// 所以它根本没有落库、建会话、归档、上报的能力。
/// 测试能实证的是它确实没去写 PLC、也没去改文件。
/// </remarks>
public class StationTrialTests
{
    // ---------- 正常读法 ----------

    [Fact]
    public async Task Reads_trigger_pallet_and_tags_without_writing_back()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1); // 触发值 1，与配置期望一致
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));
        driver.Set(300, ValueCodec.EncodeNumeric(12.4, PlcDataType.Float, FloatWordOrder.CDAB));

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith(new TagDefinition
        {
            Id = 7,
            Name = "压力",
            Address = "D300",
            DataType = PlcDataType.Float,
            Unit = "kPa",
            LowerLimit = 10,
            UpperLimit = 20,
            Enabled = true,
            PositionIndex = 0,
            IsRequired = true
        });

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        Assert.Null(result.Error);
        Assert.Equal("PLT1", result.PalletCode);
        Assert.True(result.Trigger.Matched);
        Assert.Equal((ushort)1, result.Trigger.Value);

        var tag = Assert.Single(result.Tags);
        Assert.Equal("12.4 kPa", tag.Display);
        Assert.False(tag.ReadFailed);
        Assert.False(tag.OutOfLimit);
        Assert.False(tag.FromFile);
        Assert.Contains("0x", tag.RawText);

        // 只读的硬证据：一次写都没有发生（写回响应码是采集才做的事）。
        Assert.Equal(0, driver.WriteCount);
    }

    [Fact]
    public async Task Trigger_value_that_disagrees_with_config_is_flagged()
    {
        var driver = new TrialDriver();
        driver.Set(100, 7); // 现场寄存器里是 7，而配置期望 1
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));

        await using var queue = new PlcRequestQueue(driver);
        var result = await Reader(new TrialQueueAccess { Queue = queue })
            .ReadAsync(StationWith(), Connection());

        Assert.Null(result.Error);
        Assert.False(result.Trigger.Matched);
        Assert.Equal((ushort)7, result.Trigger.Value);
        Assert.Equal((short)1, result.Trigger.Expected);
    }

    // ---------- 失败与边界 ----------

    [Fact]
    public async Task Invalid_tag_address_is_reported_as_error_instead_of_thrown()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1);
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith(new TagDefinition
        {
            Id = 9,
            Name = "坏地址",
            Address = "这不是地址",
            DataType = PlcDataType.Int16,
            Enabled = true,
            PositionIndex = 0
        });

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        // 地址配错正是试读最该被看见的情况：它必须变成结果里的一句话，而不是异常。
        Assert.NotNull(result.Error);
        Assert.False(result.Ok);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task Unavailable_queue_is_reported_with_its_reason()
    {
        var access = new TrialQueueAccess { Queue = null, Reason = "采集已关闭：PLC 连接未建立，无法试读。" };

        var result = await Reader(access).ReadAsync(StationWith(), Connection());

        Assert.Equal(access.Reason, result.Error);
    }

    [Fact]
    public async Task Tag_whose_block_cannot_be_read_is_marked_not_read()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1);
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));
        // D300 不放进驱动内存：模拟该地址读不到。

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith(new TagDefinition
        {
            Id = 3,
            Name = "温度",
            Address = "D300",
            DataType = PlcDataType.Int16,
            Unit = "℃",
            Enabled = true,
            PositionIndex = 0,
            IsRequired = true
        });

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        var tag = Assert.Single(result.Tags);
        Assert.True(tag.ReadFailed);
        // 没读到不等于超限：一个说"采集坏了"，一个说"这件不合格"，混在一起会误导现场。
        Assert.False(tag.OutOfLimit);
        Assert.Equal(1, result.ReadFailedCount);
    }

    [Fact]
    public async Task Tag_over_limit_is_flagged()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1);
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));
        driver.Set(300, ValueCodec.EncodeNumeric(25, PlcDataType.Int16, FloatWordOrder.CDAB));

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith(new TagDefinition
        {
            Id = 4,
            Name = "压力",
            Address = "D300",
            DataType = PlcDataType.Int16,
            Unit = "kPa",
            LowerLimit = 10,
            UpperLimit = 20,
            Enabled = true,
            PositionIndex = 0,
            IsRequired = true
        });

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        var tag = Assert.Single(result.Tags);
        Assert.True(tag.OutOfLimit);
        Assert.Equal("10–20", tag.LimitText);
        Assert.Equal(1, result.OutOfLimitCount);
    }

    [Fact]
    public async Task Disabled_tags_are_not_read()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1);
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith(new TagDefinition
        {
            Id = 5,
            Name = "停用点位",
            Address = "D300",
            DataType = PlcDataType.Int16,
            Enabled = false,
            PositionIndex = 0
        });

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        // 停用的点位不参与采集，也不该出现在试读里 —— 否则读数会和看板对不上。
        Assert.Empty(result.Tags);
    }

    // ---------- 文件源 ----------

    [Fact]
    public async Task File_source_tags_are_parsed_read_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dt-trial-{Guid.NewGuid():N}.json");
        const string json = """{"press":{"value":42.5}}""";
        await File.WriteAllTextAsync(path, json);

        try
        {
            var driver = new TrialDriver();
            driver.Set(100, 1);
            driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));

            await using var queue = new PlcRequestQueue(driver);
            var station = StationWith(new TagDefinition
            {
                Id = 6,
                Name = "压力",
                Address = "press.value",
                DataType = PlcDataType.Float,
                Unit = "kPa",
                Source = TagDataSource.JsonFile,
                Enabled = true,
                PositionIndex = 0
            });
            station.DataFilePath = path;
            station.DataFileFormat = DataFileFormat.Json;

            var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

            var tag = Assert.Single(result.Tags);
            Assert.True(tag.FromFile);
            Assert.False(tag.ReadFailed);
            Assert.Equal("42.5 kPa", tag.Display);

            // 试读不经 FileSourceReader —— 那个一定先归档再解析，而归档是写操作。
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task File_source_tag_missing_from_the_file_is_marked_not_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dt-trial-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{"other":1}""");

        try
        {
            var driver = new TrialDriver();
            driver.Set(100, 1);
            driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));

            await using var queue = new PlcRequestQueue(driver);
            var station = StationWith(new TagDefinition
            {
                Id = 8,
                Name = "压力",
                Address = "press.value",
                DataType = PlcDataType.Float,
                Source = TagDataSource.JsonFile,
                Enabled = true,
                PositionIndex = 0
            });
            station.DataFilePath = path;

            var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

            Assert.True(Assert.Single(result.Tags).ReadFailed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------- 曲线 ----------

    [Fact]
    public async Task Complete_curve_block_is_reported_as_read()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1);
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));
        // 4 个点、Float（2 字）、步长 2 字 → (4-1)*2 + 2 = 8 字。
        var curveWords = new ushort[8];
        for (var i = 0; i < curveWords.Length; i++)
        {
            curveWords[i] = (ushort)(i + 1);
        }

        driver.Set(500, curveWords);

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith();
        station.Curves = new List<CurveDefinition>
        {
            new()
            {
                Id = 11,
                Code = "C1",
                Name = "保压曲线",
                PointCount = 4,
                PositionIndex = 0,
                Enabled = true,
                Series = new List<CurveSeries>
                {
                    new()
                    {
                        Id = 12,
                        Name = "压力",
                        Role = SeriesRole.Y,
                        StartAddress = "D500",
                        DataType = PlcDataType.Float,
                        StrideWords = 2
                    }
                }
            }
        };

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        var curve = Assert.Single(result.Curves);
        Assert.True(curve.ReadOk);
        Assert.Equal(4, curve.PointCount);
        Assert.Equal(1, curve.SeriesCount);
        Assert.Equal("D500", curve.Address);
    }

    [Fact]
    public async Task Short_curve_block_is_reported_as_not_read()
    {
        var driver = new TrialDriver();
        driver.Set(100, 1);
        driver.Set(200, ValueCodec.EncodeAscii("PLT1", 4, highByteFirst: true));
        // 只给 4 个字，而这条曲线需要 8 个。
        driver.Set(500, 1, 2, 3, 4);

        await using var queue = new PlcRequestQueue(driver);
        var station = StationWith();
        station.Curves = new List<CurveDefinition>
        {
            new()
            {
                Id = 11,
                Code = "C1",
                Name = "保压曲线",
                PointCount = 4,
                PositionIndex = 0,
                Enabled = true,
                Series = new List<CurveSeries>
                {
                    new()
                    {
                        Id = 12,
                        Name = "压力",
                        Role = SeriesRole.Y,
                        StartAddress = "D500",
                        DataType = PlcDataType.Float,
                        StrideWords = 2
                    }
                }
            }
        };

        var result = await Reader(new TrialQueueAccess { Queue = queue }).ReadAsync(station, Connection());

        Assert.False(Assert.Single(result.Curves).ReadOk);
    }

    // ---------- 替身 ----------

    private static StationTrialReader Reader(TrialQueueAccess access)
        => new(new ThrowingScopeFactory(), access, NullLogger<StationTrialReader>.Instance);

    private static PlcConnection Connection() => new()
    {
        Id = 1,
        Name = "1号PLC",
        Brand = PlcBrand.Simulator,
        StringHighByteFirst = true,
        FloatWordOrder = FloatWordOrder.CDAB,
        MergeGapWords = 16
    };

    private static Station StationWith(params TagDefinition[] tags) => new()
    {
        Id = 1,
        Code = "ST010",
        Name = "上料",
        PlcConnectionId = 1,
        TriggerAddress = "D100",
        TriggerValue = 1,
        PalletCodeAddress = "D200",
        PalletCodeLength = 4,
        PalletCodeDataType = PlcDataType.String,
        PositionCount = 1,
        Enabled = true,
        Tags = tags.ToList()
    };

    /// <summary>按字地址给读数：没放进去的地址读回来是空的（模拟"这个地址没有"）。</summary>
    private sealed class TrialDriver : IPlcDriver
    {
        private readonly Dictionary<int, ushort> _memory = new();

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public TrialDriver Set(int offset, params ushort[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                _memory[offset + i] = values[i];
            }

            return this;
        }

        public PlcBrand Brand => PlcBrand.Simulator;

        public PlcCapabilities Capabilities => PlcCapabilities.Simulator;

        public bool IsConnected => true;

        public bool TryParseAddress(string text, out PlcAddress address)
            => new MitsubishiAddressParser().TryParse(text, out address);

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
        {
            ReadCount++;

            // 只返回"实际存在"的连续字数，不替请求方补零 —— 真实的 PLC 块短了就是短了。
            // 这条模拟是关键：曲线块读不齐、字块被截断，全靠它才测得出来。
            var available = 0;
            while (available < wordCount && _memory.ContainsKey(start.Offset + available))
            {
                available++;
            }

            if (available == 0)
            {
                return Task.FromResult(Array.Empty<ushort>());
            }

            var words = new ushort[available];
            for (var i = 0; i < available; i++)
            {
                words[i] = _memory[start.Offset + i];
            }

            return Task.FromResult(words);
        }

        public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrialQueueAccess : IPlcQueueAccess
    {
        public PlcRequestQueue? Queue { get; set; }

        public string? Reason { get; set; }

        public bool TryGetTrialQueue(int plcConnectionId, out PlcRequestQueue queue, out string? reason)
        {
            queue = Queue!;
            reason = Queue is null ? Reason : null;
            return Queue is not null;
        }
    }

    /// <summary>试读不依赖配置库：取不到快照时回落到点位默认限值，其余结论照常。</summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new NotSupportedException("测试不提供配置库");
    }
}
