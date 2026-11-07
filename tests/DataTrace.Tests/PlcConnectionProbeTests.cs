using DataTrace.Collector;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Queue;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataTrace.Tests;

/// <summary>
/// 连接页读取测试：读得对，且不写 PLC。有队列时不另开连接；没有队列时临时连一次。
/// </summary>
public class PlcConnectionProbeTests
{
    [Fact]
    public async Task Reads_through_the_collector_queue_without_writing_or_opening_another_driver()
    {
        var driver = new ProbeDriver().Set(100, 42);
        driver.IsConnected = true;
        await using var queue = new PlcRequestQueue(driver);
        var factory = new FakeFactory(driver);

        var result = await Probe(new ProbeQueueAccess { Queue = queue }, factory)
            .ReadAsync(Connection(), "D100", PlcDataType.Int16, 0);

        Assert.Null(result.Error);
        Assert.Equal("42", result.Display);
        Assert.Contains("002A", result.RawText);
        Assert.True(result.UsedCollectorQueue);
        Assert.Equal(0, driver.WriteCount);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Decodes_float_word_order_and_string_from_the_connection()
    {
        var driver = new ProbeDriver()
            .Set(300, ValueCodec.EncodeNumeric(12.5, PlcDataType.Float, FloatWordOrder.CDAB))
            .Set(400, ValueCodec.EncodeAscii("AB", 2, highByteFirst: true));
        driver.IsConnected = true;
        await using var queue = new PlcRequestQueue(driver);
        var connection = Connection();

        var probe = Probe(new ProbeQueueAccess { Queue = queue }, new FakeFactory(driver));
        var number = await probe.ReadAsync(connection, "D300", PlcDataType.Float, 0);
        var text = await probe.ReadAsync(connection, "D400", PlcDataType.String, 2);

        Assert.Null(number.Error);
        Assert.Equal("12.5", number.Display);
        Assert.Null(text.Error);
        Assert.Equal("AB", text.Display);
        Assert.Equal(0, driver.WriteCount);
    }

    [Theory]
    [InlineData("", "地址不能为空")]
    [InlineData("M100", "位地址")]
    public async Task Rejects_empty_and_bit_addresses_before_touching_the_driver(string address, string expected)
    {
        var driver = new ProbeDriver();
        var factory = new FakeFactory(driver);

        var result = await Probe(new ProbeQueueAccess(), factory)
            .ReadAsync(Connection(), address, PlcDataType.Int16, 0);

        Assert.Contains(expected, result.Error);
        Assert.Null(result.UsedCollectorQueue);
        Assert.Equal(0, driver.ReadCount);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task Without_a_queue_opens_a_temporary_connection_and_disposes_it()
    {
        var driver = new ProbeDriver().Set(100, 7);
        var factory = new FakeFactory(driver);

        var result = await Probe(new ProbeQueueAccess(), factory)
            .ReadAsync(Connection(), "D100", PlcDataType.Int16, 0);

        Assert.Null(result.Error);
        Assert.Equal("7", result.Display);
        Assert.False(result.UsedCollectorQueue);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, driver.ConnectCount);
        Assert.Equal(1, driver.DisconnectCount);
        Assert.True(driver.Disposed);
        Assert.Equal(0, driver.WriteCount);
    }

    [Fact]
    public async Task Simulator_temporary_read_does_not_disconnect_the_shared_driver()
    {
        var driver = new ProbeDriver().Set(100, 9);
        var factory = new FakeFactory(driver);

        var result = await Probe(new ProbeQueueAccess(), factory)
            .ReadAsync(Connection(PlcBrand.Simulator), "D100", PlcDataType.Int16, 0);

        Assert.Null(result.Error);
        Assert.Equal("9", result.Display);
        Assert.False(result.UsedCollectorQueue);
        Assert.Equal(1, driver.ConnectCount);
        Assert.Equal(0, driver.DisconnectCount);
        Assert.False(driver.Disposed);
        Assert.True(driver.IsConnected);
        Assert.Equal(0, driver.WriteCount);
    }

    [Fact]
    public async Task Driver_read_failure_is_returned_instead_of_thrown()
    {
        var driver = new ProbeDriver { FailReads = true };
        driver.IsConnected = true;
        await using var queue = new PlcRequestQueue(driver);

        var result = await Probe(new ProbeQueueAccess { Queue = queue }, new FakeFactory(driver))
            .ReadAsync(Connection(), "D100", PlcDataType.Int16, 0);

        Assert.Contains("模拟读取失败", result.Error);
        Assert.True(result.UsedCollectorQueue);
        Assert.Equal(0, driver.WriteCount);
    }

    private static PlcConnectionProbe Probe(IPlcQueueAccess queues, IPlcDriverFactory drivers)
        => new(queues, drivers, NullLogger<PlcConnectionProbe>.Instance);

    private static PlcConnection Connection(PlcBrand brand = PlcBrand.MitsubishiMc3E) => new()
    {
        Id = 1,
        Name = "主 PLC",
        Brand = brand,
        Host = "127.0.0.1",
        Port = 5000,
        FloatWordOrder = FloatWordOrder.CDAB,
        StringHighByteFirst = true,
        Enabled = true
    };

    private sealed class FakeFactory(ProbeDriver driver) : IPlcDriverFactory
    {
        public int CreateCount { get; private set; }

        public IPlcDriver Create(PlcConnection connection)
        {
            CreateCount++;
            return driver;
        }
    }

    private sealed class ProbeQueueAccess : IPlcQueueAccess
    {
        public PlcRequestQueue? Queue { get; set; }

        public bool TryGetTrialQueue(int plcConnectionId, out PlcRequestQueue queue, out string? reason)
        {
            queue = Queue!;
            reason = Queue is null ? "没有连接" : null;
            return Queue is not null;
        }
    }

    /// <summary>按字地址给读数，并记下连接、断开、释放和写入次数。</summary>
    private sealed class ProbeDriver : IPlcDriver
    {
        private readonly Dictionary<int, ushort> _memory = new();

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public int ConnectCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public bool Disposed { get; private set; }

        public bool FailReads { get; set; }

        public bool IsConnected { get; set; }

        public ProbeDriver Set(int offset, params ushort[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                _memory[offset + i] = values[i];
            }

            return this;
        }

        public PlcBrand Brand => PlcBrand.Simulator;

        public PlcCapabilities Capabilities => PlcCapabilities.Simulator;

        public bool TryParseAddress(string text, out PlcAddress address)
            => new MitsubishiAddressParser().TryParse(text, out address);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (FailReads)
            {
                throw new PlcDriverException("模拟读取失败");
            }

            var words = new ushort[wordCount];
            for (var i = 0; i < wordCount; i++)
            {
                words[i] = _memory.TryGetValue(start.Offset + i, out var value) ? value : (ushort)0;
            }

            return Task.FromResult(words);
        }

        public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
        {
            WriteCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
