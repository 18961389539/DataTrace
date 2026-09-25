using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using IoTClient;
using IoTClient.Clients.Modbus;
using IoTClient.Clients.PLC;
using IoTClient.Common.Enums;
using IoTClient.Enums;

namespace DataTrace.Plc.Drivers.IoTClient;

public sealed class IoTClientDriverFactory : IPhysicalPlcDriverFactory
{
    public bool CanCreate(PlcBrand brand) => brand is PlcBrand.MitsubishiMc3E or PlcBrand.SiemensS7 or PlcBrand.ModbusTcp or PlcBrand.OmronFins;

    public IPlcDriver Create(PlcConnection connection) => new IoTClientPlcDriver(connection);
}

public sealed class IoTClientPlcDriver : IPlcDriver
{
    private readonly PlcConnection _connection;
    private readonly IAddressParser _parser;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private object? _client;
    private volatile bool _connected;

    public IoTClientPlcDriver(PlcConnection connection)
    {
        _connection = connection;
        _parser = AddressParsers.For(connection.Brand);
    }

    public PlcBrand Brand => _connection.Brand;

    public PlcCapabilities Capabilities => _connection.Brand switch
    {
        PlcBrand.MitsubishiMc3E => PlcCapabilities.MitsubishiMc3E,
        PlcBrand.SiemensS7 => PlcCapabilities.SiemensS7,
        PlcBrand.ModbusTcp => PlcCapabilities.ModbusTcp,
        PlcBrand.OmronFins => PlcCapabilities.OmronFins,
        _ => PlcCapabilities.MitsubishiMc3E
    };

    public bool IsConnected => _connected;

    public bool TryParseAddress(string text, out PlcAddress address) => _parser.TryParse(text, out address);

    /// <summary>
    /// 建立连接。重连时会先把上一个 client 关掉再换新的。
    /// </summary>
    /// <remarks>
    /// IoTClient 的 client 各持一条 socket，只覆盖字段而不关闭旧 client 会让 socket 一直挂着：
    /// 反复掉线重连的现场会一处处泄露句柄，直到进程耗尽。
    /// </remarks>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CloseAndDiscardClient();
        _client = CreateClient();
        var result = Open();
        if (!result.IsSucceed)
        {
            _connected = false;
            CloseAndDiscardClient();
            throw new PlcDriverException(result.Err ?? "PLC 连接失败");
        }

        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CloseAndDiscardClient();
        _connected = false;
        return Task.CompletedTask;
    }

    public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            var address = ToClientAddress(start);
            Result<byte[]> result = _connection.Brand switch
            {
                PlcBrand.MitsubishiMc3E => ((MitsubishiClient)_client!).Read(address, (ushort)wordCount, start.IsBit),
                PlcBrand.SiemensS7 => ((SiemensClient)_client!).Read(address, (ushort)(wordCount * 2), start.IsBit),
                PlcBrand.OmronFins => ((OmronFinsClient)_client!).Read(address, (ushort)wordCount, start.IsBit, false),
                PlcBrand.ModbusTcp => ReadModbus(start, wordCount),
                _ => throw new PlcDriverException("不支持的品牌")
            };

            if (!result.IsSucceed || result.Value is null)
            {
                _connected = false;
                throw new PlcDriverException(result.Err ?? "PLC 读取失败");
            }

            var words = ToWords(result.Value, wordCount);
            // 西门子读路径会把整段响应字节倒序（IoTClient 的已知行为，见其 Read：result.Value = responseData.Reverse()）。
            // ToWords 按小端组字，正好把每个字的字节序还原，但多字读取的"字与字"顺序会整体颠倒，
            // 所以要再倒一次字序；单字没有顺序可倒。真机报文已用本地假 PLC 抓包核对过。
            if (_connection.Brand == PlcBrand.SiemensS7 && words.Length > 1)
            {
                Array.Reverse(words);
            }

            return words;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureOpenAsync(cancellationToken).ConfigureAwait(false);
            var address = ToClientAddress(start);
            if (_connection.Brand == PlcBrand.SiemensS7 && words.Length > 1)
            {
                // 与读路径同一件事的逆运算：IoTClient 写之前会把整段字节倒序，
                // 多字写入若不先倒字序，PLC 侧收到的两个字是对调的。
                var reversed = (ushort[])words.Clone();
                Array.Reverse(reversed);
                words = reversed;
            }

            var bytes = ToBytes(words);
            Result result = _connection.Brand switch
            {
                PlcBrand.MitsubishiMc3E => ((MitsubishiClient)_client!).Write(address, bytes, start.IsBit),
                PlcBrand.SiemensS7 => ((SiemensClient)_client!).Write(address, bytes, start.IsBit),
                PlcBrand.OmronFins => ((OmronFinsClient)_client!).Write(address, bytes, start.IsBit),
                PlcBrand.ModbusTcp => WriteModbus(start, words),
                _ => throw new PlcDriverException("不支持的品牌")
            };

            if (!result.IsSucceed)
            {
                _connected = false;
                throw new PlcDriverException(result.Err ?? "PLC 写入失败");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        CloseAndDiscardClient();
        _connected = false;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 读/写前的自愈连接。
    /// </summary>
    /// <remarks>
    /// 以前是 <c>ConnectAsync().GetAwaiter().GetResult()</c>：在同步阻塞里等网络超时，
    /// 既可能抽干线程池，也拿不到调用方的取消意图。这里保持全异步，并把取消传下去。
    /// </remarks>
    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (_client is null || !_connected)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>关掉并丢弃当前 client：换连接前必须做，否则旧 socket 会一直挂着。</summary>
    private void CloseAndDiscardClient()
    {
        try
        {
            Close();
        }
        catch
        {
            // 关闭失败也不能挡住重连：socket 可能已经半死，句柄由 GC 收尾。
        }

        _client = null;
    }

    private object CreateClient() => _connection.Brand switch
    {
        PlcBrand.MitsubishiMc3E => new MitsubishiClient(MitsubishiVersion.Qna_3E, _connection.Host, _connection.Port, _connection.TimeoutMs),
        PlcBrand.SiemensS7 => CreateSiemensClient(),
        PlcBrand.OmronFins => new OmronFinsClient(_connection.Host, _connection.Port, _connection.TimeoutMs),
        PlcBrand.ModbusTcp => new ModbusTcpClient(_connection.Host, _connection.Port, _connection.TimeoutMs),
        _ => throw new PlcDriverException($"不支持的品牌 {_connection.Brand}")
    };

    private Result Open() => _client switch
    {
        MitsubishiClient m => m.Open(),
        SiemensClient s => s.Open(),
        OmronFinsClient o => o.Open(),
        ModbusTcpClient t => t.Open(),
        _ => new Result { IsSucceed = false, Err = "未创建客户端" }
    };

    private void Close()
    {
        switch (_client)
        {
            case MitsubishiClient m:
                m.Close();
                break;
            case SiemensClient s:
                s.Close();
                break;
            case OmronFinsClient o:
                o.Close();
                break;
            case ModbusTcpClient t:
                t.Close();
                break;
        }
    }

    private Result<byte[]> ReadModbus(PlcAddress start, int wordCount)
    {
        var client = (ModbusTcpClient)_client!;
        var station = ParseStation();
        var function = start.Area switch
        {
            "COIL" => (byte)1,
            "DISCRETE" => (byte)2,
            "INPUT" => (byte)4,
            _ => (byte)3
        };
        return client.Read(start.Offset.ToString(), station, function, (ushort)wordCount, false);
    }

    private Result WriteModbus(PlcAddress start, ushort[] words)
    {
        var client = (ModbusTcpClient)_client!;
        var station = ParseStation();
        if (words.Length == 1)
        {
            return client.Write(start.Offset.ToString(), words[0], station, 6);
        }

        return client.Write(start.Offset.ToString(), ToModbusBytes(words), station, 16, false);
    }

    /// <summary>
    /// Modbus 多字写入的字节序：整体倒置 <see cref="ToBytes"/> 的结果。
    /// </summary>
    /// <remarks>
    /// IoTClient 的 Modbus 读路径会把响应字节数组整体倒序后再交回来，写路径却是原样发送，
    /// 两条路不是互逆运算：写进去的字序与读出来的字序对不上，大端设备会收到逐字倒置的值。
    /// 倒过来之后写/读互为逆运算，且与 <see cref="ValueCodec"/> 默认 CDAB 字序读 Float 的
    /// 既有约定一致 —— 设备按 ABCD（高字在前）存放时，读已正确、写也正确。
    /// 单字走功能码 6，由 IoTClient 自己按大端编码，本来就是对称的，不经过这里。
    /// </remarks>
    private static byte[] ToModbusBytes(ushort[] words)
    {
        var data = ToBytes(words);
        Array.Reverse(data);
        return data;
    }

    private SiemensClient CreateSiemensClient()
    {
        var (rack, slot) = ParseSiemensRackSlot();
        // 构造顺序是 (version, ip, port, slot, rack, timeout)：slot 在 rack 前面，
        // 按 (rack, slot) 原样传会把两者对调 —— 机架与插槽互换后连接握手可能就谈不成了。
        return new SiemensClient(SiemensVersion.S7_1200, _connection.Host, _connection.Port, slot, rack, _connection.TimeoutMs);
    }

    private (byte rack, byte slot) ParseSiemensRackSlot()
    {
        var extra = (_connection.Extra ?? "").Trim();
        if (extra.Length == 0)
        {
            return ((byte)0, (byte)1);
        }

        var parts = extra.Replace('/', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], out var rack)
            && int.TryParse(parts[1], out var slot))
        {
            return ((byte)rack, (byte)slot);
        }

        return ((byte)0, (byte)1);
    }

    private byte ParseStation()
    {
        if (byte.TryParse(_connection.Extra, out var station))
        {
            return station;
        }

        return 1;
    }

    private static string ToClientAddress(PlcAddress address) =>
        string.IsNullOrWhiteSpace(address.Original) ? $"{address.Area}{address.Offset}" : address.Original;

    private static ushort[] ToWords(byte[] data, int wordCount)
    {
        var words = new ushort[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            var offset = i * 2;
            if (offset + 1 < data.Length)
            {
                words[i] = (ushort)(data[offset] | (data[offset + 1] << 8));
            }
            else if (offset < data.Length)
            {
                words[i] = data[offset];
            }
        }

        return words;
    }

    private static byte[] ToBytes(ushort[] words)
    {
        var data = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
        {
            data[i * 2] = (byte)(words[i] & 0xFF);
            data[i * 2 + 1] = (byte)(words[i] >> 8);
        }

        return data;
    }
}
