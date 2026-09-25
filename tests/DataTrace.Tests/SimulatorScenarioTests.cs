using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Domain.Evaluation;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Simulator;

namespace DataTrace.Tests;

/// <summary>产线仿真数据装载：触发握手、点位取值范围、曲线写入步长与可复现性。</summary>
public class SimulatorScenarioTests
{
    private const int CurvePoints = 4;

    private static PlcConnection Connection() => new()
    {
        Id = 1,
        Name = "模拟PLC",
        Brand = PlcBrand.Simulator,
        FloatWordOrder = FloatWordOrder.CDAB,
        StringHighByteFirst = true
    };

    private static Station Station(bool includeStringTag = true, bool includeDisabled = false, int triggerValue = 1)
    {
        var tags = new List<TagDefinition>
        {
            new() { Id = 1, Code = "P", Name = "压力", Address = "D1100", DataType = PlcDataType.Float, LowerLimit = 5, UpperLimit = 20, PositionIndex = 1, Enabled = true },
            new() { Id = 2, Code = "T", Name = "温度", Address = "D1110", DataType = PlcDataType.Float, LowerLimit = 0, UpperLimit = 80, PositionIndex = 0, Enabled = true }
        };

        if (includeStringTag)
        {
            tags.Add(new TagDefinition { Id = 3, Code = "S", Name = "结果", Address = "D1120", DataType = PlcDataType.String, Length = 4, PositionIndex = 1, Enabled = true });
        }

        if (includeDisabled)
        {
            tags.Add(new TagDefinition { Id = 4, Code = "X", Name = "停用点位", Address = "D3100", DataType = PlcDataType.Float, LowerLimit = 5, UpperLimit = 20, PositionIndex = 1, Enabled = false });
        }

        var station = new Station
        {
            Id = 10,
            Code = "ST010",
            Name = "上料工站",
            Sequence = 10,
            IsFirstStation = true,
            TriggerAddress = "D1000",
            TriggerValue = (short)triggerValue,
            PalletCodeAddress = "D1010",
            PalletCodeLength = 16,
            PalletCodeDataType = PlcDataType.String,
            PositionCount = 1,
            Positions = [new ProductPositionDefinition { Index = 1, Name = "产品", OccupiedAddress = "D1020" }],
            Tags = tags,
            Curves =
            [
                new CurveDefinition
                {
                    Id = 1,
                    Code = "PD",
                    Name = "位移压力曲线",
                    PointCount = CurvePoints,
                    PositionIndex = 1,
                    Enabled = true,
                    Series =
                    [
                        new CurveSeries { Id = 1, Name = "压力", Role = SeriesRole.Y, StartAddress = "D2000", DataType = PlcDataType.Float, StrideWords = 2 },
                        new CurveSeries { Id = 2, Name = "位移", Role = SeriesRole.X, StartAddress = "D2100", DataType = PlcDataType.Float, StrideWords = 2 }
                    ]
                }
            ]
        };

        if (includeDisabled)
        {
            station.Curves.Add(new CurveDefinition
            {
                Id = 2,
                Code = "OFF",
                Name = "停用曲线",
                PointCount = CurvePoints,
                PositionIndex = 1,
                Enabled = false,
                Series = [new CurveSeries { Id = 3, Name = "压力", Role = SeriesRole.Y, StartAddress = "D3200", DataType = PlcDataType.Float, StrideWords = 2 }]
            });
        }

        return station;
    }

    private static async Task<InMemoryPlcDriver> DriverAsync()
    {
        var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        return driver;
    }

    private static float ReadFloat(InMemoryPlcDriver driver, string address, FloatWordOrder order)
    {
        var prefix = new string(address.TakeWhile(char.IsLetter).ToArray());
        var offset = int.Parse(address[prefix.Length..]);
        return ValueCodec.DecodeFloat(
            new[] { driver.GetWord($"{prefix}{offset}"), driver.GetWord($"{prefix}{offset + 1}") },
            order);
    }

    private static void Load(InMemoryPlcDriver driver, Station station, string pallet, bool injectNg = false, int seed = 5)
        => SimulatorScenario.LoadStationCycle(driver, Connection(), station, pallet, new SimulatedCycleOptions
        {
            Random = new Random(seed),
            InjectNg = injectNg
        });

    [Fact]
    public async Task Load_writes_pallet_code_occupancy_and_trigger()
    {
        await using var driver = await DriverAsync();
        var station = Station();

        Load(driver, station, "P0001");

        var palletWords = await driver.ReadWordsAsync(
            new DataTrace.Plc.Addresses.PlcAddress("D", 1010, -1, DataTrace.Plc.Addresses.AddressKind.Word, "D1010"), 8);
        Assert.Equal("P0001", ValueCodec.DecodeAscii(palletWords, 16, highByteFirst: true));

        Assert.Equal((ushort)1, driver.GetWord("D1020"));
        Assert.Equal((ushort)1, driver.GetWord("D1000"));
    }

    [Fact]
    public async Task Trigger_value_follows_station_configuration()
    {
        await using var driver = await DriverAsync();
        Load(driver, Station(triggerValue: 5), "P0001");
        Assert.Equal((ushort)5, driver.GetWord("D1000"));
    }

    [Fact]
    public async Task Numeric_tags_are_written_inside_their_limits()
    {
        await using var driver = await DriverAsync();
        var station = Station();

        Load(driver, station, "P0001");

        foreach (var tag in station.Tags.Where(t => t.Enabled && t.DataType != PlcDataType.String))
        {
            var value = ReadFloat(driver, tag.Address, FloatWordOrder.CDAB);
            Assert.False(LimitEvaluator.IsOutOfLimit(tag, value), $"{tag.Code} 仿真值 {value} 越界");
        }
    }

    private static ushort[] ReadWords(InMemoryPlcDriver driver, string address, int count)
    {
        var prefix = new string(address.TakeWhile(char.IsLetter).ToArray());
        var offset = int.Parse(address[prefix.Length..]);
        var words = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            words[i] = driver.GetWord($"{prefix}{offset + i}");
        }

        return words;
    }

    [Fact]
    public async Task Multi_word_tags_are_written_at_full_width()
    {
        await using var driver = await DriverAsync();
        var station = Station(includeStringTag: false);
        station.Tags.Clear();
        // 采集端按 WordCountOf(DataType) 读（Int32=2 字、Double=4 字），
        // 仿真必须按同一宽度写，否则读回的值是"写入值 + 残留字"拼出来的。
        station.Tags.Add(new TagDefinition { Id = 11, Code = "CNT", Name = "计数", Address = "D4000", DataType = PlcDataType.Int32, LowerLimit = 1000, UpperLimit = 100_000, PositionIndex = 1, Enabled = true });
        station.Tags.Add(new TagDefinition { Id = 12, Code = "P", Name = "压力", Address = "D4100", DataType = PlcDataType.Double, LowerLimit = 5, UpperLimit = 20, PositionIndex = 1, Enabled = true });
        station.Tags.Add(new TagDefinition { Id = 13, Code = "N", Name = "负区间", Address = "D4200", DataType = PlcDataType.Int16, LowerLimit = -40, UpperLimit = -10, PositionIndex = 1, Enabled = true });

        Load(driver, station, "P0009");

        foreach (var tag in station.Tags)
        {
            var words = ReadWords(driver, tag.Address, ValueCodec.WordCountOf(tag.DataType));
            var value = ValueCodec.DecodeNumeric(words, tag.DataType, FloatWordOrder.CDAB, 1, 0);
            Assert.False(
                LimitEvaluator.IsOutOfLimit(tag, value),
                $"{tag.Code} 读回 {value}，不在仿真区间内（写入宽度与读取宽度不一致）");
        }
    }

    [Fact]
    public async Task Int16_curve_series_is_written_as_int16()
    {
        await using var driver = await DriverAsync();
        var station = Station(includeStringTag: false);
        var curve = station.Curves.Single(c => c.Enabled);
        var y = curve.Series.Single(s => s.Role == SeriesRole.Y);
        y.DataType = PlcDataType.Int16;
        y.StrideWords = 1;

        Load(driver, station, "P0010");

        // 按 Float 写会让 Int16 序列读到浮点低位字（约等于 0），这里按 1 字整型读应当落在峰形区间内。
        var first = ValueCodec.DecodeNumeric(ReadWords(driver, y.StartAddress, 1), PlcDataType.Int16, FloatWordOrder.CDAB, 1, 0);
        Assert.InRange(first, 4, 12);
        var last = ValueCodec.DecodeNumeric(ReadWords(driver, "D2003", 1), PlcDataType.Int16, FloatWordOrder.CDAB, 1, 0);
        Assert.InRange(last, 4, 12);
    }

    [Fact]
    public async Task String_tag_receives_ok_marker()
    {
        await using var driver = await DriverAsync();
        var station = Station();

        Load(driver, station, "P0001");

        var prefix = "D";
        var words = new[] { driver.GetWord($"{prefix}1120"), driver.GetWord($"{prefix}1121") };
        Assert.Equal("OK", ValueCodec.DecodeAscii(words, 4, highByteFirst: true));
    }

    [Fact]
    public async Task Ng_injection_pushes_the_only_numeric_tag_out_of_limit()
    {
        await using var driver = await DriverAsync();
        var station = Station(includeStringTag: false);
        // 只保留一个数值点位，NG 注入目标即确定，断言不再依赖随机挑选。
        station.Tags.Remove(station.Tags.Single(t => t.Code == "T"));
        var ngTag = station.Tags.Single(t => t.DataType != PlcDataType.String);

        Load(driver, station, "P0002", injectNg: true);

        var value = ReadFloat(driver, ngTag.Address, FloatWordOrder.CDAB);
        Assert.True(LimitEvaluator.IsOutOfLimit(ngTag, value), $"{ngTag.Code} 未按预期越界：{value}");
    }

    [Fact]
    public async Task Curve_series_are_written_point_by_point_with_stride()
    {
        await using var driver = await DriverAsync();
        var station = Station();
        var curve = station.Curves.Single(c => c.Enabled);

        Load(driver, station, "P0003");

        var pressure = curve.Series.Single(s => s.Role == SeriesRole.Y);
        var displacement = curve.Series.Single(s => s.Role == SeriesRole.X);

        var yValues = new float[CurvePoints];
        var xValues = new float[CurvePoints];
        for (var i = 0; i < CurvePoints; i++)
        {
            yValues[i] = ReadFloat(driver, $"D{2000 + i * pressure.StrideWords}", FloatWordOrder.CDAB);
            xValues[i] = ReadFloat(driver, $"D{2100 + i * displacement.StrideWords}", FloatWordOrder.CDAB);
        }

        Assert.All(yValues, v => Assert.True(float.IsFinite(v)));
        // 仿真曲线为 sin 峰形：首尾点关于中值对称，和约等于 2 ✕ 基线 7.5。
        Assert.InRange(yValues[0] + yValues[^1], 14.5f, 15.5f);
        Assert.True(yValues.Max() - yValues.Min() > 1f);

        // 位移序列单调递增，覆盖 0 → 4.5mm 以上量程。
        Assert.True(xValues[0] < xValues[^1]);
        Assert.True(xValues[0] < 0.5f);
        Assert.InRange(xValues[^1], 4.5f, 5.6f);

        // 步长之外不应被写入。
        Assert.Equal((ushort)0, driver.GetWord($"D{2000 + CurvePoints * pressure.StrideWords}"));
    }

    [Fact]
    public async Task Disabled_tags_and_curves_are_left_untouched()
    {
        await using var driver = await DriverAsync();
        var station = Station(includeDisabled: true);

        Load(driver, station, "P0004");

        Assert.Equal((ushort)0, driver.GetWord("D3100"));
        Assert.Equal((ushort)0, driver.GetWord("D3200"));
    }

    [Fact]
    public async Task Same_random_seed_reproduces_identical_payload()
    {
        await using var first = await DriverAsync();
        await using var second = await DriverAsync();
        var station = Station();

        Load(first, station, "P0005", seed: 42);
        Load(second, station, "P0005", seed: 42);

        foreach (var tag in station.Tags)
        {
            Assert.Equal(first.GetWord(tag.Address), second.GetWord(tag.Address));
        }

        for (var i = 0; i < CurvePoints * 2; i += 2)
        {
            Assert.Equal(first.GetWord($"D{2000 + i}"), second.GetWord($"D{2000 + i}"));
            Assert.Equal(first.GetWord($"D{2100 + i}"), second.GetWord($"D{2100 + i}"));
        }
    }

    [Fact]
    public async Task Different_seeds_produce_different_tag_values()
    {
        await using var first = await DriverAsync();
        await using var second = await DriverAsync();
        var station = Station();

        Load(first, station, "P0006", seed: 1);
        Load(second, station, "P0006", seed: 2);

        Assert.NotEqual(first.GetWord("D1100"), second.GetWord("D1100"));
    }

    [Fact]
    public async Task Load_can_be_repeated_for_the_next_pallet()
    {
        await using var driver = await DriverAsync();
        var station = Station();

        Load(driver, station, "P0007");
        Load(driver, station, "P0008");

        var words = await driver.ReadWordsAsync(
            new DataTrace.Plc.Addresses.PlcAddress("D", 1010, -1, DataTrace.Plc.Addresses.AddressKind.Word, "D1010"), 8);
        // 同一驱动上连续装料，托盘码被覆盖为最新一板，触发位复位为触发值。
        Assert.Equal("P0008", ValueCodec.DecodeAscii(words, 16, highByteFirst: true));
        Assert.Equal((ushort)1, driver.GetWord("D1000"));
    }
}
