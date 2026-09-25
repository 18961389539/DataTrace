using DataTrace.Plc.Addresses;

namespace DataTrace.Tests;

/// <summary>四个品牌的地址解析边界：十六进制区、位寻址、别名归一化与非法输入。</summary>
public class AddressParserEdgeCaseTests
{
    private static readonly MitsubishiAddressParser Mitsubishi = new();
    private static readonly SiemensAddressParser Siemens = new();
    private static readonly ModbusAddressParser Modbus = new();
    private static readonly OmronAddressParser Omron = new();

    // ---------- 三菱 MC ----------

    [Theory]
    [InlineData("D0", "D", 0)]
    [InlineData("d100", "D", 100)]
    [InlineData("ZR100", "ZR", 100)]
    [InlineData("SD10", "SD", 10)]
    [InlineData("CN200", "CN", 200)]
    [InlineData("  D123  ", "D", 123)]
    public void Mitsubishi_parses_decimal_areas(string text, string area, int offset)
    {
        Assert.True(Mitsubishi.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(AddressKind.Word, addr.Kind);
        Assert.Equal(-1, addr.BitIndex);
    }

    [Theory]
    [InlineData("X1A", 26)]
    [InlineData("Y0", 0)]
    [InlineData("B0F", 15)]
    [InlineData("W1FF", 511)]
    [InlineData("SW10", 16)]
    public void Mitsubishi_parses_hex_areas(string text, int offset)
    {
        Assert.True(Mitsubishi.TryParse(text, out var addr));
        Assert.Equal(offset, addr.Offset);
    }

    [Theory]
    [InlineData("M10")]
    [InlineData("X1A")]
    [InlineData("Y0")]
    [InlineData("B5")]
    [InlineData("SM10")]
    [InlineData("L3")]
    [InlineData("F200")]
    public void Mitsubishi_marks_bit_areas(string text)
    {
        Assert.True(Mitsubishi.TryParse(text, out var addr));
        Assert.Equal(AddressKind.Bit, addr.Kind);
        Assert.Equal(0, addr.BitIndex);
    }

    [Theory]
    [InlineData("D100.0", 100, 0)]
    [InlineData("D100.15", 100, 15)]
    [InlineData("W10.3", 16, 3)]
    public void Mitsubishi_parses_word_bit_combo(string text, int offset, int bit)
    {
        Assert.True(Mitsubishi.TryParse(text, out var addr));
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(bit, addr.BitIndex);
        Assert.Equal(AddressKind.Bit, addr.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("D")]
    [InlineData("100")]
    [InlineData("D100.16")]
    [InlineData("D-1")]
    [InlineData("QQ10")]
    [InlineData("D10 20")]
    public void Mitsubishi_rejects_invalid_text(string? text)
        => Assert.False(Mitsubishi.TryParse(text!, out _));

    [Fact]
    public void Mitsubishi_keeps_original_text_for_diagnostics()
    {
        Assert.True(Mitsubishi.TryParse(" d100 ", out var addr));
        Assert.Equal("D100", addr.Original);
    }

    // ---------- 西门子 S7 ----------

    [Theory]
    [InlineData("DB1.DBW10", "DB1", 10, AddressKind.Word, -1)]
    [InlineData("db100.dbw0", "DB100", 0, AddressKind.Word, -1)]
    [InlineData("DB1.DBD2", "DB1", 2, AddressKind.Word, -1)]
    [InlineData("DB1.DBB4", "DB1", 4, AddressKind.Word, -1)]
    [InlineData("DB1.DBX0.0", "DB1", 0, AddressKind.Bit, 0)]
    [InlineData("DB1.DBX7.7", "DB1", 7, AddressKind.Bit, 7)]
    public void Siemens_parses_data_block_addresses(string text, string area, int offset, AddressKind kind, int bit)
    {
        Assert.True(Siemens.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(kind, addr.Kind);
        Assert.Equal(bit, addr.BitIndex);
    }

    [Theory]
    [InlineData("MW10", "M", 10)]
    [InlineData("MD20", "M", 20)]
    [InlineData("MB5", "M", 5)]
    [InlineData("M10", "M", 10)]
    [InlineData("I0", "I", 0)]
    [InlineData("Q4", "Q", 4)]
    [InlineData("E0", "I", 0)]
    [InlineData("A4", "Q", 4)]
    [InlineData("V100", "V", 100)]
    public void Siemens_parses_absolute_addresses(string text, string area, int offset)
    {
        Assert.True(Siemens.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
    }

    [Theory]
    [InlineData("I0.0", 0, 0)]
    [InlineData("Q0.1", 0, 1)]
    [InlineData("M10.2", 10, 2)]
    public void Siemens_parses_bit_suffix(string text, int offset, int bit)
    {
        Assert.True(Siemens.TryParse(text, out var addr));
        Assert.Equal(AddressKind.Bit, addr.Kind);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(bit, addr.BitIndex);
    }

    [Theory]
    [InlineData("I0.8")]
    [InlineData("M10.9")]
    [InlineData("DB1.DBX0")]
    [InlineData("DB1.DBX0.8")]
    [InlineData("DB1.DBW")]
    [InlineData("DBX0.0")]
    [InlineData("")]
    public void Siemens_rejects_invalid_text(string text)
        => Assert.False(Siemens.TryParse(text, out _));

    // ---------- Modbus ----------

    [Theory]
    [InlineData("40001", "HOLDING", 0, AddressKind.Word)]
    [InlineData("40002", "HOLDING", 1, AddressKind.Word)]
    [InlineData("40500", "HOLDING", 499, AddressKind.Word)]
    [InlineData("30001", "INPUT", 0, AddressKind.Word)]
    [InlineData("10001", "DISCRETE", 0, AddressKind.Bit)]
    [InlineData("1", "COIL", 0, AddressKind.Bit)]
    [InlineData("2", "COIL", 1, AddressKind.Bit)]
    [InlineData("0", "COIL", 0, AddressKind.Bit)]
    public void Modbus_parses_numeric_register_maps(string text, string area, int offset, AddressKind kind)
    {
        Assert.True(Modbus.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(kind, addr.Kind);
    }

    [Theory]
    [InlineData("0x9", "COIL", 9)]
    [InlineData("1x5", "DISCRETE", 5)]
    [InlineData("3x7", "INPUT", 7)]
    [InlineData("4x100", "HOLDING", 100)]
    [InlineData("4X100", "HOLDING", 100)]
    public void Modbus_parses_prefixed_forms(string text, string area, int offset)
    {
        Assert.True(Modbus.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
    }

    [Fact]
    public void Modbus_ignores_embedded_spaces()
    {
        Assert.True(Modbus.TryParse("4 0001", out var addr));
        Assert.Equal("HOLDING", addr.Area);
        Assert.Equal(0, addr.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("5x1")]
    [InlineData("2x1")]
    public void Modbus_rejects_invalid_text(string text)
        => Assert.False(Modbus.TryParse(text, out _));

    // ---------- 欧姆龙 FINS ----------

    [Theory]
    [InlineData("D100", "DM", 100)]
    [InlineData("DM100", "DM", 100)]
    [InlineData("W0", "WR", 0)]
    [InlineData("WR10", "WR", 10)]
    [InlineData("H10", "HR", 10)]
    [InlineData("HR10", "HR", 10)]
    [InlineData("A100", "AR", 100)]
    [InlineData("AR100", "AR", 100)]
    [InlineData("CIO0", "CIO", 0)]
    public void Omron_normalizes_area_aliases(string text, string area, int offset)
    {
        Assert.True(Omron.TryParse(text, out var addr));
        Assert.Equal(area, addr.Area);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(AddressKind.Word, addr.Kind);
    }

    [Theory]
    [InlineData("CIO0.0", 0, 0)]
    [InlineData("CIO0.15", 0, 15)]
    [InlineData("W5.3", 5, 3)]
    public void Omron_parses_bit_suffix(string text, int offset, int bit)
    {
        Assert.True(Omron.TryParse(text, out var addr));
        Assert.Equal(AddressKind.Bit, addr.Kind);
        Assert.Equal(offset, addr.Offset);
        Assert.Equal(bit, addr.BitIndex);
    }

    [Theory]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("100")]
    [InlineData("Z100")]
    [InlineData("D1A")]
    public void Omron_rejects_invalid_text(string text)
        => Assert.False(Omron.TryParse(text, out _));

    // ---------- 偏移单位 ----------

    [Theory]
    [InlineData("DB1.DBW10")]
    [InlineData("DB108.DBD4")]
    [InlineData("MW10")]
    [InlineData("MB5")]
    [InlineData("I64")]
    public void Siemens_word_addresses_count_offsets_in_bytes(string text)
    {
        // S7 侧 DB1.DBW10 指的是第 10 个字节。偏移一旦被当成"第 10 个字"，
        // 读计划算出的切片位置就会跑到 20 字节之后，取到的是另一段数据。
        Assert.True(Siemens.TryParse(text, out var addr));
        Assert.Equal(OffsetUnit.Byte, addr.OffsetUnit);
    }

    [Theory]
    [InlineData("D100")]
    [InlineData("W1A")]
    [InlineData("ZR100")]
    public void Mitsubishi_word_addresses_count_offsets_in_words(string text)
    {
        Assert.True(Mitsubishi.TryParse(text, out var addr));
        Assert.Equal(OffsetUnit.Word, addr.OffsetUnit);
    }

    [Theory]
    [InlineData("D100")]
    [InlineData("CIO100")]
    public void Omron_word_addresses_count_offsets_in_words(string text)
    {
        Assert.True(Omron.TryParse(text, out var addr));
        Assert.Equal(OffsetUnit.Word, addr.OffsetUnit);
    }

    [Fact]
    public void Modbus_word_addresses_count_offsets_in_words()
    {
        Assert.True(Modbus.TryParse("40011", out var addr));
        Assert.Equal(OffsetUnit.Word, addr.OffsetUnit);
    }

    // ---------- 公共契约 ----------

    [Fact]
    public void Parsers_are_independent_per_brand()
    {
        // 同一串文本在不同品牌下语义不同，解析器之间不得互相兜底。
        Assert.True(Mitsubishi.TryParse("D100", out var mc));
        Assert.True(Omron.TryParse("D100", out var fins));
        Assert.Equal("D", mc.Area);
        Assert.Equal("DM", fins.Area);

        // Modbus 不认 "D100"，三菱不认纯数字寄存器。
        Assert.False(Modbus.TryParse("D100", out _));
        Assert.False(Mitsubishi.TryParse("40001", out _));
    }

    [Fact]
    public void Parsed_address_is_a_value_object()
    {
        Assert.True(Mitsubishi.TryParse("D100", out var a));
        Assert.True(Mitsubishi.TryParse("D100", out var b));
        Assert.Equal(a, b);
        Assert.True(a == b);
    }
}
