using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace DataTrace.Tests;

/// <summary>
/// 最小 S7 从站：只回"握手成功 + 读到的字节"，用来核对驱动发出的报文与解析回来的字。
/// </summary>
/// <remarks>
/// 现场没有西门子 PLC，但请求报文里的 DB 块号、区码、起始偏移（按位计）和长度是协议规定的，
/// 可以据此把"偏移算错、字序算错"这类问题在本地钉死。服务端不校验报文语义，
/// 只把整包读走并按请求里的字节数回一段可预测的数据。
/// </remarks>
internal sealed class FakeSiemensS7Server : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public FakeSiemensS7Server()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>收到过的请求报文（含 TPKT 头），按顺序记录。</summary>
    public List<byte[]> Requests { get; } = [];

    /// <summary>只保留读/写数据请求（握手报文之外的那条）。</summary>
    public IReadOnlyList<byte[]> DataRequests
    {
        get
        {
            lock (Requests)
            {
                return Requests.Where(r => r.Length > 17 && r[17] is 0x04 or 0x05).ToList();
            }
        }
    }

    /// <summary>连接建立次数。</summary>
    public int ConnectionCount { get; private set; }

    /// <summary>回给客户端的字，按 PLC 侧顺序（大端字节）上线；默认第 i 个字 = 0x1000 + i。</summary>
    public Func<int, ushort> WordAt { get; set; } = i => (ushort)(0x1000 + i);

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
            while (!_cts.IsCancellationRequested)
            {
                // TPKT：版本(0) 保留(1) 长度(2-3)，长度含头本身。
                var header = new byte[4];
                if (!await ReadExactAsync(stream, header, _cts.Token).ConfigureAwait(false))
                {
                    return;
                }

                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                if (length < 4 || length > 512)
                {
                    return;
                }

                var body = new byte[length - 4];
                if (!await ReadExactAsync(stream, body, _cts.Token).ConfigureAwait(false))
                {
                    return;
                }

                var request = header.Concat(body).ToArray();
                lock (Requests)
                {
                    Requests.Add(request);
                }

                var reply = BuildResponse(request);
                await stream.WriteAsync(reply, _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 回一条 TPKT 报文：IoTClient 只从头两个字节取长度、从尾部取数据，
    /// 因此数据放在整包末尾即可，长度按请求里的"访问数据个数（字节）"回。
    /// </summary>
    private byte[] BuildResponse(byte[] request)
    {
        if (request.Length > 17 && request[17] == 0x05)
        {
            // 写应答：IoTClient 只看整包最后一个字节是否为 0xFF。
            var ack = new byte[22];
            ack[0] = 0x03;
            ack[1] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2), (ushort)ack.Length);
            ack[15] = 0x05;
            ack[16] = 0x00;
            ack[21] = 0xFF;
            return ack;
        }

        // 读请求 [23][24] 是长度（字节）；这里只处理读，握手报文回一段固定长度即可。
        var byteCount = request.Length > 24 && request[17] == 0x04
            ? BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(23))
            : 4;
        byteCount = Math.Clamp(byteCount, 1, 512);

        var payload = new byte[26 + byteCount];
        // IoTClient 会检查包内固定位置的"异常代码"，这里一律给成功。
        if (payload.Length > 22)
        {
            payload[15] = 0x04;
            payload[16] = 0x01;
            payload[17] = 0xFF;
        }

        // 数据段：第 i 个字按大端（高字节在前）落两个字。
        var dataStart = payload.Length - byteCount;
        for (var i = 0; i + 1 < byteCount; i += 2)
        {
            var word = WordAt(i / 2);
            payload[dataStart + i] = (byte)(word >> 8);
            payload[dataStart + i + 1] = (byte)word;
        }

        var packet = new byte[4 + payload.Length];
        packet[0] = 0x03;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
        payload.CopyTo(packet, 4);
        return packet;
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
}