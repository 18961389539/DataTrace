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

    public static ushort[] EncodeFloat(float value, FloatWordOrder order)
    {
        Span<byte> le = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(le, value);
        // IEEE little-endian bytes: D C B A
        byte d = le[0], c = le[1], b = le[2], a = le[3];
        var be = order switch
        {
            FloatWordOrder.ABCD => (a, b, c, d),
            FloatWordOrder.BADC => (b, a, d, c),
            FloatWordOrder.CDAB => (c, d, a, b),
            FloatWordOrder.DCBA => (d, c, b, a),
            _ => (a, b, c, d)
        };

        return
        [
            (ushort)((be.Item1 << 8) | be.Item2),
            (ushort)((be.Item3 << 8) | be.Item4)
        ];
    }

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
}
