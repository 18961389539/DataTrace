using DataTrace.Domain.Enums;
using DataTrace.Plc.Codec;

namespace DataTrace.Tests;

public class ValueCodecTests
{
    // ---------- 字长 ----------

    [Theory]
    [InlineData(PlcDataType.Bool, 0, 1)]
    [InlineData(PlcDataType.Int16, 0, 1)]
    [InlineData(PlcDataType.Int32, 0, 2)]
    [InlineData(PlcDataType.Float, 0, 2)]
    [InlineData(PlcDataType.Double, 0, 4)]
    [InlineData(PlcDataType.String, 16, 8)]
    [InlineData(PlcDataType.String, 17, 9)]
    [InlineData(PlcDataType.String, 1, 1)]
    [InlineData(PlcDataType.String, 3, 2)]
    [InlineData(PlcDataType.String, 0, 1)]
    public void WordCount_of_types(PlcDataType type, int stringLength, int expected)
        => Assert.Equal(expected, ValueCodec.WordCountOf(type, stringLength));

    // ---------- 单精度 ----------

    [Theory]
    [InlineData(FloatWordOrder.ABCD)]
    [InlineData(FloatWordOrder.BADC)]
    [InlineData(FloatWordOrder.CDAB)]
    [InlineData(FloatWordOrder.DCBA)]
    public void Float_roundtrip(FloatWordOrder order)
    {
        const float expected = 1.0f;
        var words = ValueCodec.EncodeFloat(expected, order);
        var actual = ValueCodec.DecodeFloat(words, order);
        Assert.Equal(expected, actual, precision: 5);
    }

    [Theory]
    [InlineData(FloatWordOrder.ABCD)]
    [InlineData(FloatWordOrder.BADC)]
    [InlineData(FloatWordOrder.CDAB)]
    [InlineData(FloatWordOrder.DCBA)]
    public void Float_roundtrip_across_value_range(FloatWordOrder order)
    {
        float[] samples =
        [
            0f, -0f, 1.5f, -1.5f, 273.15f, -273.15f, 3.4028235e38f, 1.17549435e-38f,
            0.1f, 12345.678f, -98765.4321f, 6.02e23f
        ];

        foreach (var value in samples)
        {
            var words = ValueCodec.EncodeFloat(value, order);
            // 同一字序编解码必须无损；BitConverter 比较可避免 -0 与精度误差干扰。
            Assert.Equal(
                BitConverter.SingleToInt32Bits(value),
                BitConverter.SingleToInt32Bits(ValueCodec.DecodeFloat(words, order)));
        }
    }

    [Theory]
    [InlineData(FloatWordOrder.ABCD)]
    [InlineData(FloatWordOrder.BADC)]
    [InlineData(FloatWordOrder.CDAB)]
    [InlineData(FloatWordOrder.DCBA)]
    public void Float_nan_and_infinity_roundtrip(FloatWordOrder order)
    {
        Assert.True(float.IsNaN(ValueCodec.DecodeFloat(ValueCodec.EncodeFloat(float.NaN, order), order)));
        Assert.True(float.IsPositiveInfinity(ValueCodec.DecodeFloat(ValueCodec.EncodeFloat(float.PositiveInfinity, order), order)));
        Assert.True(float.IsNegativeInfinity(ValueCodec.DecodeFloat(ValueCodec.EncodeFloat(float.NegativeInfinity, order), order)));
    }

    [Fact]
    public void Float_1_0_word_pattern_matches_ieee754_byte_order()
    {
        // 1.0f 的 IEEE-754 大端字节为 3F 80 00 00。
        Assert.Equal(new ushort[] { 0x3F80, 0x0000 }, ValueCodec.EncodeFloat(1.0f, FloatWordOrder.ABCD));
        Assert.Equal(new ushort[] { 0x803F, 0x0000 }, ValueCodec.EncodeFloat(1.0f, FloatWordOrder.BADC));
        Assert.Equal(new ushort[] { 0x0000, 0x3F80 }, ValueCodec.EncodeFloat(1.0f, FloatWordOrder.CDAB));
        Assert.Equal(new ushort[] { 0x0000, 0x803F }, ValueCodec.EncodeFloat(1.0f, FloatWordOrder.DCBA));
    }

    [Fact]
    public void Different_word_orders_produce_different_words()
    {
        var abcd = ValueCodec.EncodeFloat(3.14f, FloatWordOrder.ABCD);
        var dcba = ValueCodec.EncodeFloat(3.14f, FloatWordOrder.DCBA);
        Assert.NotEqual(abcd, dcba);
    }

    [Fact]
    public void Decode_float_with_insufficient_words_returns_zero()
    {
        Assert.Equal(0f, ValueCodec.DecodeFloat([], FloatWordOrder.ABCD));
        Assert.Equal(0f, ValueCodec.DecodeFloat([0x3F80], FloatWordOrder.ABCD));
    }

    // ---------- 定长整数 ----------

    [Fact]
    public void Decode_int16_sign_extends()
    {
        Assert.Equal(-5d, ValueCodec.DecodeNumeric(ValueCodec.EncodeInt16(-5), PlcDataType.Int16, FloatWordOrder.ABCD, 1, 0));
        Assert.Equal(32767d, ValueCodec.DecodeNumeric([0x7FFF], PlcDataType.Int16, FloatWordOrder.ABCD, 1, 0));
        Assert.Equal(-32768d, ValueCodec.DecodeNumeric([0x8000], PlcDataType.Int16, FloatWordOrder.ABCD, 1, 0));
    }

    [Fact]
    public void Decode_int32_combines_two_words_big_endian()
    {
        Assert.Equal(65538d, ValueCodec.DecodeNumeric([0x0001, 0x0002], PlcDataType.Int32, FloatWordOrder.ABCD, 1, 0));
        Assert.Equal(-1d, ValueCodec.DecodeNumeric([0xFFFF, 0xFFFF], PlcDataType.Int32, FloatWordOrder.ABCD, 1, 0));
    }

    [Fact]
    public void Decode_int32_falls_back_to_int16_when_truncated()
    {
        Assert.Equal(-3d, ValueCodec.DecodeNumeric([unchecked((ushort)(-3))], PlcDataType.Int32, FloatWordOrder.ABCD, 1, 0));
    }

    [Fact]
    public void Decode_bool_maps_non_zero_to_one()
    {
        Assert.Equal(1d, ValueCodec.DecodeNumeric([1], PlcDataType.Bool, FloatWordOrder.ABCD, 1, 0));
        Assert.Equal(1d, ValueCodec.DecodeNumeric([0x00FF], PlcDataType.Bool, FloatWordOrder.ABCD, 1, 0));
        Assert.Equal(0d, ValueCodec.DecodeNumeric([0], PlcDataType.Bool, FloatWordOrder.ABCD, 1, 0));
    }

    [Fact]
    public void Decode_double_consumes_four_words()
    {
        // 1.0 的双精度大端字节为 3F F0 00 00 00 00 00 00。
        Assert.Equal(1d, ValueCodec.DecodeNumeric([0x3FF0, 0x0000, 0x0000, 0x0000], PlcDataType.Double, FloatWordOrder.ABCD, 1, 0));
        // DCBA 字序下按字节整体倒排（A/B 取自首字，C/D 取自次字）。
        Assert.Equal(1d, ValueCodec.DecodeNumeric([0x0000, 0xF03F, 0x0000, 0x0000], PlcDataType.Double, FloatWordOrder.DCBA, 1, 0));
        Assert.Equal(0d, ValueCodec.DecodeNumeric([0x3FF0, 0x0000], PlcDataType.Double, FloatWordOrder.ABCD, 1, 0));
    }

    [Fact]
    public void Decode_numeric_returns_zero_for_empty_words()
    {
        foreach (var type in new[] { PlcDataType.Bool, PlcDataType.Int16, PlcDataType.Int32, PlcDataType.Float, PlcDataType.Double })
        {
            Assert.Equal(0d, ValueCodec.DecodeNumeric([], type, FloatWordOrder.ABCD, 1, 0));
        }
    }

    [Fact]
    public void Decode_numeric_applies_scale_then_offset()
    {
        var words = ValueCodec.EncodeFloat(10f, FloatWordOrder.CDAB);
        // raw * scale + offset
        Assert.Equal(21d, ValueCodec.DecodeNumeric(words, PlcDataType.Float, FloatWordOrder.CDAB, 2, 1), precision: 5);
        Assert.Equal(0.1d, ValueCodec.DecodeNumeric(words, PlcDataType.Float, FloatWordOrder.CDAB, 0.01, 0), precision: 5);
        Assert.Equal(-10d, ValueCodec.DecodeNumeric(ValueCodec.EncodeInt16(10), PlcDataType.Int16, FloatWordOrder.ABCD, -1, 0));
    }

    // ---------- ASCII 字符串 ----------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ascii_roundtrip_for_both_byte_orders(bool highByteFirst)
    {
        var words = ValueCodec.EncodeAscii("P0001", 16, highByteFirst);
        Assert.Equal(8, words.Length);
        Assert.Equal("P0001", ValueCodec.DecodeAscii(words, 16, highByteFirst));
    }

    [Fact]
    public void Ascii_high_byte_first_word_pattern()
    {
        var words = ValueCodec.EncodeAscii("AB", 4, highByteFirst: true);
        Assert.Equal(new ushort[] { 0x4142, 0x0000 }, words);

        var swapped = ValueCodec.EncodeAscii("AB", 4, highByteFirst: false);
        Assert.Equal(new ushort[] { 0x4241, 0x0000 }, swapped);
    }

    [Fact]
    public void Ascii_truncates_when_value_longer_than_declared_length()
    {
        var words = ValueCodec.EncodeAscii("HELLO-WORLD", 5, highByteFirst: true);
        Assert.Equal(3, words.Length);
        Assert.Equal("HELLO", ValueCodec.DecodeAscii(words, 5, highByteFirst: true));
    }

    [Fact]
    public void Ascii_replaces_non_ascii_with_question_mark()
    {
        // PLC 侧只认 7bit ASCII，中文一律退化为 '?'，避免静默写出乱码。
        var words = ValueCodec.EncodeAscii("中文", 8, highByteFirst: true);
        Assert.Equal("??", ValueCodec.DecodeAscii(words, 8, highByteFirst: true));
    }

    [Fact]
    public void Ascii_decoding_stops_at_embedded_nul()
    {
        var words = ValueCodec.EncodeAscii("A\0B", 4, highByteFirst: true);
        Assert.Equal("A", ValueCodec.DecodeAscii(words, 4, highByteFirst: true));
    }

    [Fact]
    public void Ascii_decoding_handles_empty_inputs()
    {
        Assert.Equal("", ValueCodec.DecodeAscii([], 16, true));
        Assert.Equal("", ValueCodec.DecodeAscii(ValueCodec.EncodeAscii("AB", 4, true), 0, true));
        Assert.Equal("", ValueCodec.DecodeAscii(ValueCodec.EncodeAscii("AB", 4, true), -1, true));
    }

    [Fact]
    public void Ascii_decoding_clamps_to_available_words()
    {
        // 声明长度大于实际字长时不越界，只解出已有字。
        var words = ValueCodec.EncodeAscii("AB", 2, highByteFirst: true);
        Assert.Single(words);
        Assert.Equal("AB", ValueCodec.DecodeAscii(words, 16, highByteFirst: true));
    }

    [Fact]
    public void Ascii_encoding_pads_short_value_with_nul()
    {
        var words = ValueCodec.EncodeAscii("A", 4, highByteFirst: true);
        Assert.Equal(new ushort[] { 0x4100, 0x0000 }, words);
        Assert.Equal("A", ValueCodec.DecodeAscii(words, 4, highByteFirst: true));
    }

    [Fact]
    public void Ascii_encoding_tolerates_null_value()
    {
        var words = ValueCodec.EncodeAscii(null!, 4, highByteFirst: true);
        Assert.All(words, w => Assert.Equal((ushort)0, w));
        Assert.Equal("", ValueCodec.DecodeAscii(words, 4, highByteFirst: true));
    }

    [Fact]
    public void Ascii_roundtrip_preserves_odd_length_payload()
    {
        // 奇数长度会落在一个字的低半字节，读回时须裁掉补位。
        const string code = "P-12345";
        var words = ValueCodec.EncodeAscii(code, code.Length, highByteFirst: true);
        Assert.Equal((code.Length + 1) / 2, words.Length);
        Assert.Equal(code, ValueCodec.DecodeAscii(words, code.Length, highByteFirst: true));
    }
}
