using System.Buffers.Binary;
using System.Text;
using DataTrace.Domain.Enums;

namespace DataTrace.Plc.Codec;

public static class ValueCodec
{
    public static int WordCountOf(PlcDataType dataType, int stringLength = 0) => dataType switch
    {
        PlcDataType.Bool => 1,
        PlcDataType.Int16 => 1,
        PlcDataType.Int32 => 2,
        PlcDataType.Float => 2,
        PlcDataType.Double => 4,
        PlcDataType.String => Math.Max(1, (stringLength + 1) / 2),
        _ => 1
    };

    public static double DecodeNumeric(
        ReadOnlySpan<ushort> words,
        PlcDataType dataType,
        FloatWordOrder floatOrder,
        double scale,
        double offset)
    {
        double raw = dataType switch
        {
            PlcDataType.Bool => words.Length > 0 && words[0] != 0 ? 1 : 0,
            PlcDataType.Int16 => words.Length > 0 ? (short)words[0] : 0,
            PlcDataType.Int32 => DecodeInt32(words),
            PlcDataType.Float => DecodeFloat(words, floatOrder),
            PlcDataType.Double => DecodeDouble(words, floatOrder),
            _ => 0
        };

        return raw * scale + offset;
    }

    public static string DecodeAscii(ReadOnlySpan<ushort> words, int length, bool highByteFirst)
    {
        if (length <= 0 || words.IsEmpty)
        {
            return "";
        }

        var chars = new char[Math.Min(length, words.Length * 2)];
        var n = 0;
        foreach (var word in words)
        {
            var high = (char)((word >> 8) & 0xFF);
            var low = (char)(word & 0xFF);
            var first = highByteFirst ? high : low;
            var second = highByteFirst ? low : high;
            if (n < chars.Length)
            {
                chars[n++] = first;
            }

            if (n < chars.Length)
            {
                chars[n++] = second;
            }
        }

        var text = new string(chars, 0, n).TrimEnd('\0').Trim();
        var zero = text.IndexOf('\0');
        return zero >= 0 ? text[..zero] : text;
    }

    public static ushort[] EncodeInt16(short value) => [(ushort)value];

    /// <summary>32 位整数按"高字在前"编码（与 <see cref="DecodeInt32"/> 互逆）。</summary>
    public static ushort[] EncodeInt32(int value) => [(ushort)((value >> 16) & 0xFFFF), (ushort)(value & 0xFFFF)];

    public static ushort[] EncodeFloat(float value, FloatWordOrder order)
    {
        Span<byte> ieeeBe = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(ieeeBe, value);
        var (w0, w1) = PackWord(ieeeBe[0], ieeeBe[1], ieeeBe[2], ieeeBe[3], order);
        return [w0, w1];
    }

    /// <summary>双精度按两对 float 字序编码（与 <see cref="DecodeDouble"/> 互逆）。</summary>
    public static ushort[] EncodeDouble(double value, FloatWordOrder order)
    {
        Span<byte> ieeeBe = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(ieeeBe, value);
        var (hi0, hi1) = PackWord(ieeeBe[0], ieeeBe[1], ieeeBe[2], ieeeBe[3], order);
        var (lo0, lo1) = PackWord(ieeeBe[4], ieeeBe[5], ieeeBe[6], ieeeBe[7], order);
        return [hi0, hi1, lo0, lo1];
    }

    /// <summary>
    /// 按数据类型把数值编码成寄存器字（与 <see cref="DecodeNumeric"/> 互逆）。
    /// </summary>
    /// <remarks>
    /// 写侧必须按类型给足字宽：Int32 是 2 字、Double 是 4 字。按"统一 1 字/2 字"写，
    /// 读侧（按 <see cref="WordCountOf"/> 取数）就会把残留字拼进来，读回的值与写入值不是一回事。
    /// </remarks>
    public static ushort[] EncodeNumeric(double value, PlcDataType dataType, FloatWordOrder order) => dataType switch
    {
        PlcDataType.Bool => [(ushort)(value != 0 ? 1 : 0)],
        PlcDataType.Int16 => EncodeInt16((short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue)),
        PlcDataType.Int32 => EncodeInt32((int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)),
        PlcDataType.Float => EncodeFloat((float)value, order),
        PlcDataType.Double => EncodeDouble(value, order),
        _ => []
    };

    public static ushort[] EncodeAscii(string value, int length, bool highByteFirst)
    {
        var padded = (value ?? "").PadRight(length, '\0');
        if (padded.Length > length)
        {
            padded = padded[..length];
        }

        var bytes = Encoding.ASCII.GetBytes(padded.Select(ch => ch <= 127 ? ch : '?').ToArray());
        if (bytes.Length < length)
        {
            Array.Resize(ref bytes, length);
        }

        var wordCount = (length + 1) / 2;
        var words = new ushort[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            var b0 = i * 2 < bytes.Length ? bytes[i * 2] : (byte)0;
            var b1 = i * 2 + 1 < bytes.Length ? bytes[i * 2 + 1] : (byte)0;
            words[i] = highByteFirst
                ? (ushort)((b0 << 8) | b1)
                : (ushort)((b1 << 8) | b0);
        }

        return words;
    }

    public static float DecodeFloat(ReadOnlySpan<ushort> words, FloatWordOrder order)
    {
        if (words.Length < 2)
        {
            return 0;
        }

        var ieeeBe = ToIeeeBigEndian(words[0], words[1], order);
        Span<byte> le = stackalloc byte[4];
        le[0] = ieeeBe[3];
        le[1] = ieeeBe[2];
        le[2] = ieeeBe[1];
        le[3] = ieeeBe[0];
        return BinaryPrimitives.ReadSingleLittleEndian(le);
    }

    private static double DecodeDouble(ReadOnlySpan<ushort> words, FloatWordOrder order)
    {
        if (words.Length < 4)
        {
            return 0;
        }

        // 双精度按两对 float 字序处理：前两字、后两字各自按同一字序。
        var hi = ToIeeeBigEndian(words[0], words[1], order);
        var lo = ToIeeeBigEndian(words[2], words[3], order);
        Span<byte> be = stackalloc byte[8];
        hi.CopyTo(be);
        lo.CopyTo(be[4..]);
        Span<byte> le = stackalloc byte[8];
        for (var i = 0; i < 8; i++)
        {
            le[i] = be[7 - i];
        }

        return BinaryPrimitives.ReadDoubleLittleEndian(le);
    }

    private static int DecodeInt32(ReadOnlySpan<ushort> words)
    {
        if (words.Length < 2)
        {
            return words.Length > 0 ? (short)words[0] : 0;
        }

        return (words[0] << 16) | words[1];
    }

    private static byte[] ToIeeeBigEndian(ushort w0, ushort w1, FloatWordOrder order)
    {
        byte a = (byte)(w0 >> 8);
        byte b = (byte)(w0 & 0xFF);
        byte c = (byte)(w1 >> 8);
        byte d = (byte)(w1 & 0xFF);
        return order switch
        {
            FloatWordOrder.ABCD => [a, b, c, d],
            FloatWordOrder.BADC => [b, a, d, c],
            FloatWordOrder.CDAB => [c, d, a, b],
            FloatWordOrder.DCBA => [d, c, b, a],
            _ => [a, b, c, d]
        };
    }

    /// <summary>
    /// 把 4 个 IEEE 大端字节按字序还原成两个寄存器字（<see cref="ToIeeeBigEndian"/> 的逆）。
    /// 单精度与双精度共用，避免两处各写一份字序映射、改了一边。
    /// </summary>
    private static (ushort W0, ushort W1) PackWord(byte a, byte b, byte c, byte d, FloatWordOrder order) => order switch
    {
        FloatWordOrder.ABCD => ((ushort)((a << 8) | b), (ushort)((c << 8) | d)),
        FloatWordOrder.BADC => ((ushort)((b << 8) | a), (ushort)((d << 8) | c)),
        FloatWordOrder.CDAB => ((ushort)((c << 8) | d), (ushort)((a << 8) | b)),
        FloatWordOrder.DCBA => ((ushort)((d << 8) | c), (ushort)((b << 8) | a)),
        _ => ((ushort)((a << 8) | b), (ushort)((c << 8) | d))
    };
}
