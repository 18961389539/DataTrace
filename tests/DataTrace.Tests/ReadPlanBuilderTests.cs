using DataTrace.Plc.Addresses;
using DataTrace.Plc.Planning;
using DataTrace.Plc.Simulator;

namespace DataTrace.Tests;

public class ReadPlanBuilderTests
{
    private static readonly MitsubishiAddressParser Parser = new();

    private static readonly SiemensAddressParser Siemens = new();

    private static PlcAddress Addr(string text)
    {
        Assert.True(Parser.TryParse(text, out var address), $"地址 {text} 应当可解析");
        return address;
    }

    private static AddressReadRequest Req(string key, string address, int wordCount)
        => new() { Key = key, Address = Addr(address), WordCount = wordCount };

    private static AddressReadRequest SiemensReq(string key, string address, int wordCount)
    {
        Assert.True(Siemens.TryParse(address, out var parsed), $"地址 {address} 应当可解析");
        return new AddressReadRequest { Key = key, Address = parsed, WordCount = wordCount };
    }

    [Fact]
    public void Merges_nearby_registers_and_splits_by_max()
    {
        var plan = ReadPlanBuilder.Build(
        [
            Req("a", "D100", 2),
            Req("b", "D102", 2),
            Req("c", "D200", 2),
            Req("curve", "D3000", 2500)
        ], maxWordsPerRead: 960, mergeGapWords: 16);

        Assert.True(plan.Blocks.Count >= 3);
        Assert.Contains(plan.Blocks, b => b.StartOffset == 100 && b.WordCount >= 4);
        Assert.True(plan.Items["curve"].Count >= 3);
        Assert.Equal(2500, plan.Items["curve"].Sum(s => s.WordCount));
    }

    [Fact]
    public void Empty_requests_produce_empty_plan()
    {
        var plan = ReadPlanBuilder.Build([], maxWordsPerRead: 960, mergeGapWords: 16);
        Assert.Empty(plan.Blocks);
        Assert.Empty(plan.Items);
    }

    [Fact]
    public void Bit_addresses_and_zero_length_requests_are_skipped()
    {
        var plan = ReadPlanBuilder.Build(
        [
            new AddressReadRequest { Key = "bit", Address = Addr("M10"), WordCount = 1 },
            new AddressReadRequest { Key = "empty", Address = Addr("D100"), WordCount = 0 },
            new AddressReadRequest { Key = "negative", Address = Addr("D200"), WordCount = -1 },
            Req("real", "D300", 1)
        ], maxWordsPerRead: 960, mergeGapWords: 16);

        // M10 是位地址、其余长度为非法值，只有 real 进入计划。
        Assert.Single(plan.Blocks);
        Assert.Equal(300, plan.Blocks[0].StartOffset);
        Assert.Single(plan.Items);
        Assert.True(plan.Items.ContainsKey("real"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_max_words_throws(int maxWords)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => ReadPlanBuilder.Build([Req("a", "D100", 1)], maxWords, mergeGapWords: 16));

    [Fact]
    public void Long_request_is_split_into_max_sized_blocks()
    {
        var plan = ReadPlanBuilder.Build([Req("long", "D100", 10)], maxWordsPerRead: 4, mergeGapWords: 16);

        Assert.Equal(3, plan.Blocks.Count);
        Assert.Equal(new[] { 4, 4, 2 }, plan.Blocks.Select(b => b.WordCount).ToArray());
        Assert.Equal(new[] { 100, 104, 108 }, plan.Blocks.Select(b => b.StartOffset).ToArray());
        Assert.Equal(3, plan.Items["long"].Count);
        Assert.Equal(10, plan.Items["long"].Sum(s => s.WordCount));
    }

    [Fact]
    public void Nearby_items_merge_when_gap_is_within_threshold()
    {
        var merged = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D110", 2)], maxWordsPerRead: 960, mergeGapWords: 16);
        Assert.Single(merged.Blocks);
        Assert.Equal(100, merged.Blocks[0].StartOffset);
        Assert.Equal(12, merged.Blocks[0].WordCount);

        var separate = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D110", 2)], maxWordsPerRead: 960, mergeGapWords: 4);
        Assert.Equal(2, separate.Blocks.Count);
    }

    [Fact]
    public void Merge_threshold_is_inclusive()
    {
        // 第二个点位起点恰好等于 前一点位结束 + mergeGap 时仍属于同一簇。
        var plan = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D118", 2)], maxWordsPerRead: 960, mergeGapWords: 16);
        Assert.Single(plan.Blocks);

        var beyond = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D119", 2)], maxWordsPerRead: 960, mergeGapWords: 16);
        Assert.Equal(2, beyond.Blocks.Count);
    }

    [Fact]
    public void Zero_gap_only_merges_touching_items()
    {
        var touching = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D102", 1)], maxWordsPerRead: 960, mergeGapWords: 0);
        Assert.Single(touching.Blocks);

        var gap = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D103", 1)], maxWordsPerRead: 960, mergeGapWords: 0);
        Assert.Equal(2, gap.Blocks.Count);
    }

    [Fact]
    public void Different_areas_never_share_a_block()
    {
        // D 与 W 都是字区域，即使偏移相同也必须拆成两块分别读取。
        var plan = ReadPlanBuilder.Build(
            [Req("d", "D100", 2), Req("w", "W100", 2)], maxWordsPerRead: 960, mergeGapWords: 64);

        Assert.Equal(2, plan.Blocks.Count);
        Assert.Equal(new[] { "D", "W" }, plan.Blocks.Select(b => b.Area).ToArray());
    }

    [Fact]
    public void Area_grouping_is_case_insensitive()
    {
        // 驱动层大小写归一化不同，解析器可能产出 'd'/'D' 混用，需按同区合并。
        var plan = ReadPlanBuilder.Build(
        [
            new AddressReadRequest { Key = "a", Address = new PlcAddress("d", 100, -1, AddressKind.Word, "d100"), WordCount = 2 },
            new AddressReadRequest { Key = "b", Address = new PlcAddress("D", 102, -1, AddressKind.Word, "D102"), WordCount = 2 }
        ], maxWordsPerRead: 960, mergeGapWords: 16);

        Assert.Single(plan.Blocks);
        Assert.Equal(4, plan.Blocks[0].WordCount);
    }

    [Fact]
    public void Items_spanning_a_chunk_boundary_are_split_into_multiple_slices()
    {
        var plan = ReadPlanBuilder.Build(
            [Req("a", "D100", 2), Req("b", "D103", 3)], maxWordsPerRead: 4, mergeGapWords: 16);

        // 簇 100..106 被切成 100..104 与 104..106 两块。
        Assert.Equal(2, plan.Blocks.Count);
        Assert.Single(plan.Items["a"]);
        Assert.Equal(2, plan.Items["b"].Count);

        var bSlices = plan.Items["b"];
        Assert.Equal(new[] { (0, 3, 1), (1, 0, 2) }, bSlices.Select(s => (s.BlockIndex, s.OffsetInBlock, s.WordCount)));
    }

    [Fact]
    public void Duplicate_keys_accumulate_slices_in_request_order()
    {
        var plan = ReadPlanBuilder.Build(
            [Req("dup", "D100", 2), Req("dup", "D200", 2)], maxWordsPerRead: 960, mergeGapWords: 16);

        Assert.Equal(2, plan.Items["dup"].Count);
        Assert.Equal(4, plan.Items["dup"].Sum(s => s.WordCount));
    }

    [Fact]
    public void Item_lookup_is_case_insensitive_and_unknown_keys_return_empty()
    {
        var plan = ReadPlanBuilder.Build([Req("Curve_X", "D100", 2)], maxWordsPerRead: 960, mergeGapWords: 16);

        var buffers = new[] { new ushort[] { 7, 8 } };
        Assert.Equal(new ushort[] { 7, 8 }, plan.GetWords("curve_x", buffers));
        Assert.Equal(new ushort[] { 7, 8 }, plan.GetWords("CURVE_X", buffers));
        Assert.Empty(plan.GetWords("missing", buffers));
    }

    [Fact]
    public async Task Planned_blocks_read_from_driver_reassemble_original_values()
    {
        // 端到端校验：分块读取后按切片拼回，必须与原寄存器内容逐字一致。
        var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        for (var i = 0; i < 10; i++)
        {
            driver.SetWord($"D{100 + i}", (ushort)(i + 1));
        }

        var plan = ReadPlanBuilder.Build([Req("block", "D100", 10)], maxWordsPerRead: 4, mergeGapWords: 16);
        var buffers = new ushort[plan.Blocks.Count][];
        for (var i = 0; i < plan.Blocks.Count; i++)
        {
            var block = plan.Blocks[i];
            buffers[i] = await driver.ReadWordsAsync(block.StartAddress(), block.WordCount);
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (ushort)i), plan.GetWords("block", buffers));
    }

    // ---------- 西门子：偏移按字节 ----------

    [Fact]
    public void Byte_unit_items_never_share_a_block()
    {
        // DB108.DBW0（0 字节处）与 DB108.DBW4（4 字节处）相隔 4 字节。
        // 字单位下 mergeGapWords=16 会把它们合成一块并按"字数"算切片；字节单位必须各读各的，
        // 否则第二个点位会取到 8 字节之后的数据（每个点位都是错的，但看起来很合理）。
        var plan = ReadPlanBuilder.Build(
        [
            SiemensReq("a", "DB108.DBW0", 1),
            SiemensReq("b", "DB108.DBW4", 1)
        ], maxWordsPerRead: 240, mergeGapWords: 16);

        Assert.Equal(2, plan.Blocks.Count);
        Assert.All(plan.Blocks, b => Assert.Equal(OffsetUnit.Byte, b.OffsetUnit));
        Assert.Equal(new[] { 0, 4 }, plan.Blocks.Select(b => b.StartOffset).ToArray());
        Assert.All(plan.Blocks, b => Assert.Equal(1, b.WordCount));
        Assert.Equal(0, plan.Items["a"].Single().OffsetInBlock);
        Assert.Equal(0, plan.Items["b"].Single().OffsetInBlock);
    }

    [Fact]
    public void Byte_unit_long_items_are_split_by_words_but_stepped_by_bytes()
    {
        // 240 字上限 = 480 字节：1000 字的读请求必须按"480 字节"步进切块，
        // 按 240 步进的话第二块会从第 240 字节开始，与前一块重叠一半。
        var plan = ReadPlanBuilder.Build([SiemensReq("curve", "DB108.DBW0", 1000)], maxWordsPerRead: 240, mergeGapWords: 16);

        Assert.Equal(5, plan.Blocks.Count);
        Assert.Equal(new[] { 0, 480, 960, 1440, 1920 }, plan.Blocks.Select(b => b.StartOffset).ToArray());
        Assert.Equal(new[] { 240, 240, 240, 240, 40 }, plan.Blocks.Select(b => b.WordCount).ToArray());
        Assert.Equal(1000, plan.Items["curve"].Sum(s => s.WordCount));
    }

    [Fact]
    public void Byte_unit_items_do_not_merge_with_word_unit_items_of_the_same_area()
    {
        // 同名区（例如都叫 D）但单位不同，偏移的步长不是一回事，绝不能进同一块。
        var plan = ReadPlanBuilder.Build(
        [
            Req("word", "D100", 2),
            new AddressReadRequest
            {
                Key = "byte",
                Address = new PlcAddress("D", 100, -1, AddressKind.Word, "D100", OffsetUnit.Byte),
                WordCount = 2
            }
        ], maxWordsPerRead: 240, mergeGapWords: 16);

        Assert.Equal(2, plan.Blocks.Count);
        Assert.Equal(OffsetUnit.Word, plan.Blocks[0].OffsetUnit);
        Assert.Equal(OffsetUnit.Byte, plan.Blocks[1].OffsetUnit);
    }

    [Fact]
    public void Byte_unit_block_start_address_uses_iotclient_db_syntax()
    {
        var plan = ReadPlanBuilder.Build([SiemensReq("a", "DB108.DBW4", 2)], maxWordsPerRead: 240, mergeGapWords: 16);
        var start = plan.Blocks.Single().StartAddress();

        // DB 区必须写成 DB108.4：写成 DB1084 会被 IoTClient 解析成"DB 块号 1084、偏移 84 字节"。
        Assert.Equal("DB108.4", start.Original);
        Assert.Equal("DB108", start.Area);
        Assert.Equal(4, start.Offset);
        Assert.Equal(OffsetUnit.Byte, start.OffsetUnit);

        // 非 DB 区（M/I/Q）保留"区名+偏移"的写法，IoTClient 对此按字节解释。
        var m = ReadPlanBuilder.Build([SiemensReq("m", "MW10", 1)], maxWordsPerRead: 240, mergeGapWords: 16);
        Assert.Equal("M10", m.Blocks.Single().StartAddress().Original);
    }

    [Fact]
    public void Word_unit_block_start_address_keeps_brand_syntax()
    {
        var plan = ReadPlanBuilder.Build([Req("a", "D100", 2)], maxWordsPerRead: 240, mergeGapWords: 16);
        Assert.Equal("D100", plan.Blocks.Single().StartAddress().Original);
        Assert.Equal(OffsetUnit.Word, plan.Blocks.Single().StartAddress().OffsetUnit);
    }
}

public class AddressParserTests
{
    [Theory]
    [InlineData("D100", "D", 100)]
    [InlineData("M10", "M", 10)]
    [InlineData("W1A", "W", 0x1A)]
    public void Mitsubishi(string text, string area, int offset)
    {
        var parser = new MitsubishiAddressParser();
        Assert.True(parser.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
    }

    [Fact]
    public void Siemens_db()
    {
        var parser = new SiemensAddressParser();
        Assert.True(parser.TryParse("DB1.DBW10", out var addr));
        Assert.Equal("DB1", addr.Area);
        Assert.Equal(10, addr.Offset);
    }

    [Fact]
    public void Modbus_holding()
    {
        var parser = new ModbusAddressParser();
        Assert.True(parser.TryParse("40001", out var addr));
        Assert.Equal("HOLDING", addr.Area);
        Assert.Equal(0, addr.Offset);
    }
}
