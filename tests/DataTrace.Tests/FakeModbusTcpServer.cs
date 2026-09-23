using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DataTrace.Tests;

/// <summary>
/// 最小 Modbus/TCP 从站：只实现 IPlcDriver 用到的 1/2/3/4/6/16 号功能码。
/// 保持寄存器按协议惯例以大端上线（高字节在前），因此可以用"写入后读回"来检验驱动的组帧与字节序是否自洽。
/// </summary>
internal sealed class FakeModbusTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public FakeModbusTcpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public ushort[] Holding { get; } = new ushort[256];

    public byte[] Coils { get; } = new byte[256];

    /// <summary>服务端回给客户端的寄存器字节，用于精确断言驱动的小端组字行为。</summary>
    public byte[]? RawReadOverride { get; set; }

    /// <summary>收到过的 PDU 首字节（功能码），按顺序记录。</summary>
    public List<byte> FunctionCodes { get; } = [];

    /// <summary>最后一次读请求解析出的 (单元号, 起始寄存器, 点数)。</summary>
    public (byte Unit, int Address, int Count)? LastRead { get; private set; }

    /// <summary>最后一次写请求解析出的 (单元号, 起始寄存器, 原始字节)。</summary>
    public (byte Unit, int Address, byte[] Value)? LastWrite { get; private set; }

    public int ConnectionCount { get; private set; }

    /// <summary>置为 true 后，服务端在回完当前请求立刻断开，用来验证重连路径。</summary>
    public bool DropConnectionAfterResponse { get; set; }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            ConnectionCount++;
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var header = new byte[7];
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, _cts.Token).ConfigureAwait(false))
                {
                    return;
                }

                // MBAP：事务标识(0-1) 协议标识(2-3) 后续字节数(4-5) 单元标识(6)。
                // 后续字节数含单元标识本身，故 PDU 长度要减 1。
                var pduLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1;
                if (pduLength <= 0 || pduLength > 260)
                {
                    return;
                }

                var pdu = new byte[pduLength];
                if (!await ReadExactAsync(stream, pdu, _cts.Token).ConfigureAwait(false))
                {
                    return;
                }

                var reply = Handle(header, pdu);
                if (reply is null)
                {
                    return;
                }

                await stream.WriteAsync(reply, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);

                if (DropConnectionAfterResponse)
                {
                    return;
                }
            }
        }
    }

    private byte[]? Handle(byte[] header, byte[] pdu)
    {
        var unit = header[6];
        var function = pdu[0];
        lock (FunctionCodes)
        {
            FunctionCodes.Add(function);
        }

        byte[] responsePdu = function switch
        {
            1 => ReadBits(pdu),
            2 => ReadBits(pdu),
            3 => ReadHolding(pdu, unit),
            4 => ReadHolding(pdu, unit),
            6 => WriteSingle(pdu, unit),
            16 => WriteMultiple(pdu, unit),
            _ => ExceptionPdu(function, 0x01)
        };

        if (responsePdu.Length == 0)
        {
            return null;
        }

        return Frame(header, responsePdu);
    }

    private byte[] ReadHolding(byte[] pdu, byte unit)
    {
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        LastRead = (unit, address, count);

        var data = RawReadOverride ?? BuildBigEndianBytes(address, count);
        var body = new byte[2 + data.Length];
        body[0] = pdu[0];
        body[1] = (byte)data.Length;
        data.CopyTo(body, 2);
        return body;
    }

    private byte[] BuildBigEndianBytes(int start, int count)
    {
        var data = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var index = start + i;
            var value = index < Holding.Length ? Holding[index] : (ushort)0;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2), value);
        }

        return data;
    }

    private byte[] ReadBits(byte[] pdu)
    {
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        var byteCount = (count + 7) / 8;
        var body = new byte[2 + byteCount];
        body[0] = pdu[0];
        body[1] = (byte)byteCount;
        for (var i = 0; i < count; i++)
        {
            var index = address + i;
            if (index < Coils.Length && Coils[index] != 0)
            {
                body[2 + i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return body;
    }

    private byte[] WriteSingle(byte[] pdu, byte unit)
    {
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        LastWrite = (unit, address, pdu[3..5]);
        if (address < Holding.Length)
        {
            Holding[address] = value;
        }

        // 功能码 6 的正常响应就是请求 PDU 的回显。
        return pdu[..5];
    }

    private byte[] WriteMultiple(byte[] pdu, byte unit)
    {
        var address = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        var valueBytes = pdu.AsSpan(6, Math.Min(pdu.Length - 6, count * 2)).ToArray();
        LastWrite = (unit, address, valueBytes);
        for (var i = 0; i + 1 < valueBytes.Length; i += 2)
        {
            var index = address + i / 2;
            if (index < Holding.Length)
            {
                Holding[index] = BinaryPrimitives.ReadUInt16BigEndian(valueBytes.AsSpan(i));
            }
        }

        return pdu[..5];
    }

    private static byte[] ExceptionPdu(byte function, byte code) => [(byte)(function | 0x80), code];

    private static byte[] Frame(byte[] requestHeader, byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        // 事务标识与单元号原样回显，客户端据此匹配请求。
        frame[0] = requestHeader[0];
        frame[1] = requestHeader[1];
        frame[6] = requestHeader[6];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        pdu.CopyTo(frame, 7);
        return frame;
    }

    /// <summary>按大端把寄存器值写进服务端内存，供读取类断言预置数据。</summary>
    public void SetHolding(int startAddress, params ushort[] values) => values.CopyTo(Holding, startAddress);

    public string HoldingDump(int start, int count)
    {
        var sb = new StringBuilder();
        for (var i = start; i < start + count; i++)
        {
            sb.Append(Holding[i].ToString("X4")).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch
        {
            // 关闭监听时正在排空的连接会抛异常，忽略
        }

        // 故意不释放 _cts：派生的服务任务仍持有它的 Token。
    }

    /// <summary>取一个无人监听的端口，用于验证连接失败路径。</summary>
    public static int GetClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
