using System.Net;
using System.Net.Sockets;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Drivers.IoTClient;

namespace DataTrace.Tests;

/// <summary>
/// IoTClient 物理驱动：品牌能力矩阵、地址解析器选型、连接失败与断线重连，
/// 以及对 FakeModbusTcpServer 的真实读写组帧。
/// </summary>
public class IoTClientDriverTests
{
    private const int TimeoutMs = 1500;

    private static PlcConnection Connection(PlcBrand brand, int port, string? extra = null) => new()
    {
        Id = 1,
        Name = $"{brand}",
        Brand = brand,
        Host = IPAddress.Loopback.ToString(),
        Port = port,
        TimeoutMs = TimeoutMs,
        Extra = extra
    };

    // ---------- 工厂与能力矩阵 ----------

    [Theory]
    [InlineData(PlcBrand.MitsubishiMc3E, true)]
    [InlineData(PlcBrand.SiemensS7, true)]
    [InlineData(PlcBrand.ModbusTcp, true)]
    [InlineData(PlcBrand.OmronFins, true)]
    [InlineData(PlcBrand.Simulator, false)]
    public void Factory_CanCreate_onlyForPhysicalBrands(PlcBrand brand, bool expected)
        => Assert.Equal(expected, new IoTClientDriverFactory().CanCreate(brand));

    [Fact]
    public void Factory_Create_returnsDriverCarryingBrand()
    {
        var driver = new IoTClientDriverFactory().Create(Connection(PlcBrand.SiemensS7, FakeModbusTcpServer.GetClosedPort()));
        Assert.IsType<IoTClientPlcDriver>(driver);
        Assert.Equal(PlcBrand.SiemensS7, driver.Brand);
    }

    [Theory]
    [InlineData(PlcBrand.MitsubishiMc3E, 960, 960)]
    [InlineData(PlcBrand.SiemensS7, 240, 240)]
    [InlineData(PlcBrand.ModbusTcp, 125, 123)]
    [InlineData(PlcBrand.OmronFins, 999, 999)]
    public void Capabilities_matchBrandChunkLimits(PlcBrand brand, int read, int write)
    {
        var caps = new IoTClientPlcDriver(Connection(brand, FakeModbusTcpServer.GetClosedPort())).Capabilities;
        Assert.Equal(read, caps.MaxWordsPerRead);
        Assert.Equal(write, caps.MaxWordsPerWrite);
    }

    [Fact]
    public void Capabilities_unknownBrandFallsBackToMitsubishi()
    {
        var caps = new IoTClientPlcDriver(Connection((PlcBrand)99, FakeModbusTcpServer.GetClosedPort())).Capabilities;
        Assert.Same(PlcCapabilities.MitsubishiMc3E, caps);
    }

    // ---------- 地址解析器按品牌选型 ----------

    [Theory]
    [InlineData(PlcBrand.ModbusTcp, "40001", "HOLDING", 0, AddressKind.Word)]
    [InlineData(PlcBrand.ModbusTcp, "30005", "INPUT", 4, AddressKind.Word)]
    [InlineData(PlcBrand.ModbusTcp, "10002", "DISCRETE", 1, AddressKind.Bit)]
    [InlineData(PlcBrand.MitsubishiMc3E, "D100", "D", 100, AddressKind.Word)]
    public void TryParseAddress_usesParserForBrand(PlcBrand brand, string text, string area, int offset, AddressKind kind)
    {
        Assert.True(new IoTClientPlcDriver(Connection(brand, FakeModbusTcpServer.GetClosedPort())).TryParseAddress(text, out var address));
        Assert.Equal(area, address.Area);
        Assert.Equal(offset, address.Offset);
        Assert.Equal(kind, address.Kind);
    }

    [Fact]
    public void TryParseAddress_rejectsOtherBrandsSyntax()
    {
        var modbus = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, FakeModbusTcpServer.GetClosedPort()));
        Assert.False(modbus.TryParseAddress("D100", out _));

        var mitsubishi = new IoTClientPlcDriver(Connection(PlcBrand.MitsubishiMc3E, FakeModbusTcpServer.GetClosedPort()));
        Assert.False(mitsubishi.TryParseAddress("40001", out _));
    }

    [Fact]
    public void TryParseAddress_keepsOriginalTextForPhysicalClients()
    {
        var mitsubishi = new IoTClientPlcDriver(Connection(PlcBrand.MitsubishiMc3E, FakeModbusTcpServer.GetClosedPort()));
        Assert.True(mitsubishi.TryParseAddress(" d100 ", out var address));
        Assert.Equal("D100", address.Original);
    }

    // ---------- 连接生命周期 ----------

    [Theory]
    [InlineData(PlcBrand.MitsubishiMc3E)]
    [InlineData(PlcBrand.SiemensS7)]
    [InlineData(PlcBrand.OmronFins)]
    [InlineData(PlcBrand.ModbusTcp)]
    public async Task ConnectAsync_toClosedPort_throwsDriverException(PlcBrand brand)
    {
        await using var driver = new IoTClientPlcDriver(Connection(brand, FakeModbusTcpServer.GetClosedPort()));
        Assert.False(driver.IsConnected);

        var ex = await Assert.ThrowsAsync<PlcDriverException>(() => driver.ConnectAsync());
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_cancellationRequested_failsFast()
    {
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, FakeModbusTcpServer.GetClosedPort()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ConnectAsync(cts.Token));
    }

    [Fact]
    public async Task DisconnectAsync_withoutConnect_isSafe()
    {
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, FakeModbusTcpServer.GetClosedPort()));
        await driver.DisconnectAsync();
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public async Task DisposeAsync_isIdempotent()
    {
        var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, FakeModbusTcpServer.GetClosedPort()));
        await driver.DisposeAsync();
        await driver.DisposeAsync();
    }

    // ---------- Modbus 真实读写 ----------

    [Fact]
    public async Task Modbus_connect_setsIsConnected()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));

        await driver.ConnectAsync();

        Assert.True(driver.IsConnected);
    }

    [Fact]
    public async Task Modbus_read_establishesConnection()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        await driver.ReadWordsAsync(address, 1);

        Assert.Equal(1, server.ConnectionCount);
    }

    [Fact]
    public async Task Modbus_read_singleRegister_preservesBigEndianValue()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        server.SetHolding(0, 0x1234);
        Assert.True(driver.TryParseAddress("40001", out var address));

        var words = await driver.ReadWordsAsync(address, 1);

        Assert.Equal(new ushort[] { 0x1234 }, words);
    }

    [Fact]
    public async Task Modbus_read_multiWord_returnsRegistersInReversedWordOrder()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        server.SetHolding(0, 0x1122, 0x3344);
        Assert.True(driver.TryParseAddress("40001", out var address));

        var words = await driver.ReadWordsAsync(address, 2);

        // IoTClient 把整个响应字节数组反转，驱动再按小端组字：单寄存器值原样保留，
        // 但多字读取的字序被整体倒置。
        Assert.Equal(new ushort[] { 0x3344, 0x1122 }, words);
    }

    [Fact]
    public async Task Modbus_floatReadWithDefaultCdabOrder_decodesBigEndianDeviceValue()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        // 12.5f = 0x41480000，按 ABCD（高字在前）落在两个寄存器上。
        server.SetHolding(0, 0x4148, 0x0000);
        Assert.True(driver.TryParseAddress("40001", out var address));

        var words = await driver.ReadWordsAsync(address, 2);
        var decoded = ValueCodec.DecodeFloat(words, FloatWordOrder.CDAB);

        // 驱动倒置字序 + 默认 CDAB 再倒回来，二者合成后正好还原设备的 ABCD 值。
        Assert.Equal(12.5f, decoded, 6);
        Assert.NotEqual(decoded, ValueCodec.DecodeFloat(words, FloatWordOrder.ABCD));
    }

    [Fact]
    public async Task Modbus_read_sendsParsedOffsetAsStartAddress()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40011", out var address));
        await driver.ReadWordsAsync(address, 1);

        Assert.Equal(10, server.LastRead!.Value.Address);
    }

    [Fact]
    public async Task Modbus_read_inputArea_usesFunctionCode4()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("30002", out var address));
        await driver.ReadWordsAsync(address, 1);

        Assert.Equal((byte)4, server.FunctionCodes.Single());
        Assert.Equal(1, server.LastRead!.Value.Address);
    }

    [Fact]
    public async Task Modbus_read_usesStationFromExtra()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port, extra: "17"));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        await driver.ReadWordsAsync(address, 1);

        Assert.Equal((byte)17, server.LastRead!.Value.Unit);
    }

    [Fact]
    public async Task Modbus_read_defaultsStationToOne_whenExtraNotNumeric()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port, extra: "rack=3"));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        await driver.ReadWordsAsync(address, 1);

        Assert.Equal((byte)1, server.LastRead!.Value.Unit);
    }

    [Fact]
    public async Task Modbus_writeMultiple_storesWordsInReversedOrder()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        await driver.WriteWordsAsync(address, new ushort[] { 0x0001, 0xA5A5, 0xFFFF });

        Assert.Equal((byte)16, server.FunctionCodes.Single());
        // 与读路径互逆：设备按大端收到的是倒序后的字，也就是它自己的存放顺序。
        Assert.Equal("FFFF A5A5 0001", server.HoldingDump(0, 3));
    }

    [Fact]
    public async Task Modbus_writeThenRead_multipleWordsRoundTrip()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        var written = new ushort[] { 0x0001, 0xA5A5, 0xFFFF };
        await driver.WriteWordsAsync(address, written);

        var words = await driver.ReadWordsAsync(address, written.Length);

        Assert.Equal(written, words);
    }

    [Fact]
    public async Task Modbus_floatWriteThenRead_roundTripsAndLandsBigEndianOnDevice()
    {
        await using var server = new FakeModbusTcpServer();
        var connection = Connection(PlcBrand.ModbusTcp, server.Port);
        await using var driver = new IoTClientPlcDriver(connection);
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        var words = ValueCodec.EncodeFloat(12.5f, connection.FloatWordOrder);
        await driver.WriteWordsAsync(address, words);

        // 12.5f = 0x41480000：默认 CDAB 编码 + 写路径倒序，落到大端设备上正好是 ABCD。
        Assert.Equal("4148 0000", server.HoldingDump(0, 2));

        var readBack = await driver.ReadWordsAsync(address, 2);
        Assert.Equal(12.5f, ValueCodec.DecodeFloat(readBack, connection.FloatWordOrder), 6);
    }

    [Fact]
    public async Task Modbus_writeSingle_usesFunctionCode6()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40003", out var address));
        await driver.WriteWordsAsync(address, new ushort[] { 0x1234 });

        Assert.Equal((byte)6, server.FunctionCodes.Single());
        Assert.Equal(2, server.LastWrite!.Value.Address);

        // 回写结果码走的就是这条路（采集流水线只写单字），存进设备的必须就是原值。
        Assert.Equal("1234", server.HoldingDump(2, 1));
        var readBack = await driver.ReadWordsAsync(address, 1);
        Assert.Equal(new ushort[] { 0x1234 }, readBack);
    }

    [Fact]
    public async Task Modbus_read_afterServerGone_throwsAndMarksDisconnected()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        await driver.ReadWordsAsync(address, 1);
        Assert.True(driver.IsConnected);

        await server.DisposeAsync();

        var ex = await Assert.ThrowsAsync<PlcDriverException>(() => driver.ReadWordsAsync(address, 1));
        Assert.False(driver.IsConnected);
        Assert.Contains("读取", ex.Message);
    }

    [Fact]
    public async Task Modbus_read_afterDisconnect_reconnectsAutomatically()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        await driver.ReadWordsAsync(address, 1);
        await driver.DisconnectAsync();
        Assert.False(driver.IsConnected);

        var words = await driver.ReadWordsAsync(address, 1);

        Assert.Single(words);
        Assert.True(driver.IsConnected);
        Assert.Equal(2, server.ConnectionCount);
    }

    [Fact]
    public async Task Modbus_readWords_truncatedResponse_padsRemainingWordsWithZero()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        // 请求 3 个字但只回 3 个字节：反转后末字取到单字节，第 3 字补零。
        server.RawReadOverride = [0x11, 0x22, 0x33];
        Assert.True(driver.TryParseAddress("40001", out var address));

        var words = await driver.ReadWordsAsync(address, 3);

        Assert.Equal(new ushort[] { 0x2233, 0x0011, 0x0000 }, words);
    }

    [Fact]
    public async Task Modbus_concurrentReads_areSerializedByGate()
    {
        await using var server = new FakeModbusTcpServer();
        await using var driver = new IoTClientPlcDriver(Connection(PlcBrand.ModbusTcp, server.Port));
        await driver.ConnectAsync();

        Assert.True(driver.TryParseAddress("40001", out var address));
        var reads = Enumerable.Range(0, 8).Select(_ => driver.ReadWordsAsync(address, 1)).ToList();
        var results = await Task.WhenAll(reads);

        Assert.All(results, words => Assert.Single(words));
        Assert.Equal(8, server.FunctionCodes.Count);
    }

    // ---------- 未注册品牌 ----------

    [Fact]
    public async Task UnknownBrand_readThrowsWithoutCreatingClient()
    {
        await using var driver = new IoTClientPlcDriver(Connection((PlcBrand)99, FakeModbusTcpServer.GetClosedPort()));
        Assert.True(new MitsubishiAddressParser().TryParse("D1", out var address));

        await Assert.ThrowsAsync<PlcDriverException>(() => driver.ReadWordsAsync(address, 1));
        await Assert.ThrowsAsync<PlcDriverException>(() => driver.WriteWordsAsync(address, [1]));
    }
}
