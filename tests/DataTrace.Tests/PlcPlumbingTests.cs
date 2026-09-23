using System.Diagnostics;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Factory;
using DataTrace.Plc.Queue;
using DataTrace.Plc.Simulator;

namespace DataTrace.Tests;

/// <summary>内存模拟驱动：连接状态、读写失败开关、编解码与并发安全。</summary>
public class InMemoryPlcDriverTests
{
    private static PlcAddress Addr(string text)
    {
        var driver = new InMemoryPlcDriver();
        Assert.True(driver.TryParseAddress(text, out var address));
        return address;
    }

    [Fact]
    public async Task Starts_disconnected_and_blocks_io_until_connected()
    {
        await using var driver = new InMemoryPlcDriver();
        Assert.False(driver.IsConnected);
        Assert.Equal(PlcBrand.Simulator, driver.Brand);
        Assert.Equal(PlcCapabilities.Simulator.MaxWordsPerRead, driver.Capabilities.MaxWordsPerRead);
        Assert.Equal(TimeSpan.Zero, driver.SimulatedLatency);

        await Assert.ThrowsAsync<PlcDriverException>(() => driver.ReadWordsAsync(Addr("D100"), 1));
        await Assert.ThrowsAsync<PlcDriverException>(() => driver.WriteWordsAsync(Addr("D100"), [1]));
    }

    [Fact]
    public async Task Connect_and_disconnect_toggle_state_and_gate_io()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        Assert.True(driver.IsConnected);

        await driver.DisconnectAsync();
        Assert.False(driver.IsConnected);
        await Assert.ThrowsAsync<PlcDriverException>(() => driver.ReadWordsAsync(Addr("D100"), 1));
    }

    [Fact]
    public async Task Word_read_write_roundtrip_and_defaults_to_zero()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();

        driver.SetWord("D100", 1234);
        driver.SetWord("D102", 5678);

        var words = await driver.ReadWordsAsync(Addr("D100"), 4);
        Assert.Equal(new ushort[] { 1234, 0, 5678, 0 }, words);
        Assert.Equal((ushort)1234, driver.GetWord("D100"));
        Assert.Equal((ushort)0, driver.GetWord("D999"));
        Assert.Equal((ushort)1234, (await driver.ReadWordsAsync(Addr("D100"), 1))[0]);
    }

    [Fact]
    public async Task Write_words_persists_contiguous_block()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();

        await driver.WriteWordsAsync(Addr("D200"), [11, 22, 33]);
        var read = await driver.ReadWordsAsync(Addr("D199"), 5);
        Assert.Equal(new ushort[] { 0, 11, 22, 33, 0 }, read);
    }

    [Fact]
    public async Task Int16_and_float_helpers_write_expected_words()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();

        driver.SetInt16("D0", -1);
        Assert.Equal(new ushort[] { 0xFFFF }, await driver.ReadWordsAsync(Addr("D0"), 1));

        driver.SetFloat("D10", 1.5f, FloatWordOrder.CDAB);
        var words = await driver.ReadWordsAsync(Addr("D10"), 2);
        Assert.Equal(1.5f, ValueCodec.DecodeFloat(words, FloatWordOrder.CDAB));
    }

    [Fact]
    public async Task Ascii_helper_writes_multiple_words()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();

        driver.SetAscii("D300", "P0001", 16, highByteFirst: true);
        var words = await driver.ReadWordsAsync(Addr("D300"), 8);
        Assert.Equal("P0001", ValueCodec.DecodeAscii(words, 16, highByteFirst: true));

        driver.SetAscii("D400", "P0002", 16, highByteFirst: false);
        var swapped = await driver.ReadWordsAsync(Addr("D400"), 8);
        Assert.Equal("P0002", ValueCodec.DecodeAscii(swapped, 16, highByteFirst: false));
    }

    [Fact]
    public async Task Trigger_writes_handshake_value()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();

        driver.Trigger("D1000");
        Assert.Equal((ushort)1, driver.GetWord("D1000"));

        driver.Trigger("D1000", 5);
        Assert.Equal((ushort)5, driver.GetWord("D1000"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZZ100")]
    [InlineData("D")]
    public void Invalid_address_throws_argument_exception(string text)
    {
        var driver = new InMemoryPlcDriver();
        Assert.Throws<ArgumentException>(() => driver.SetWord(text, 1));
        Assert.Throws<ArgumentException>(() => driver.GetWord(text));
        Assert.Throws<ArgumentException>(() => driver.SetFloat(text, 1f, FloatWordOrder.ABCD));
        Assert.Throws<ArgumentException>(() => driver.SetAscii(text, "A", 2));
    }

    [Fact]
    public void TryParse_address_reflects_underlying_parser()
    {
        var driver = new InMemoryPlcDriver();
        Assert.True(driver.TryParseAddress("D100", out var address));
        Assert.Equal("D", address.Area);
        Assert.False(driver.TryParseAddress("nope", out _));
    }

    [Fact]
    public async Task Failure_switches_surface_as_plc_driver_exception()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        driver.SetWord("D100", 7);

        driver.SetFailReads(true);
        await Assert.ThrowsAsync<PlcDriverException>(() => driver.ReadWordsAsync(Addr("D100"), 1));

        driver.SetFailReads(false);
        driver.SetFailWrites(true);
        await Assert.ThrowsAsync<PlcDriverException>(() => driver.WriteWordsAsync(Addr("D100"), [9]));

        driver.SetFailWrites(false);
        Assert.Equal((ushort)7, (await driver.ReadWordsAsync(Addr("D100"), 1))[0]);
    }

    [Fact]
    public async Task Simulated_latency_delays_each_io()
    {
        await using var driver = new InMemoryPlcDriver(TimeSpan.FromMilliseconds(40));
        await driver.ConnectAsync();

        var sw = Stopwatch.StartNew();
        await driver.ReadWordsAsync(Addr("D100"), 1);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds >= 25, $"耗时 {sw.ElapsedMilliseconds}ms 短于预期延迟");
    }

    [Fact]
    public async Task Concurrent_io_is_serialized_and_lossless()
    {
        await using var driver = new InMemoryPlcDriver(TimeSpan.FromMilliseconds(1));
        await driver.ConnectAsync();

        var writes = Enumerable.Range(0, 24)
            .Select(i => driver.WriteWordsAsync(Addr($"D{1000 + i * 2}"), [(ushort)(i + 1)]))
            .ToArray();
        await Task.WhenAll(writes);

        var reads = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => driver.ReadWordsAsync(Addr($"D{1000 + i * 2}"), 1)));
        for (var i = 0; i < 24; i++)
        {
            Assert.Equal((ushort)(i + 1), reads[i][0]);
        }
    }

    [Fact]
    public async Task Disposal_clears_connection_state()
    {
        var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        await driver.DisposeAsync();
        Assert.False(driver.IsConnected);
    }
}

public class SimulatorCatalogTests
{
    [Fact]
    public void Same_id_yields_same_driver_instance()
    {
        var catalog = new SimulatorCatalog();
        var first = catalog.Get(1);
        var second = catalog.Get(1);
        var other = catalog.Get(2);

        Assert.Same(first, second);
        Assert.NotSame(first, other);
        Assert.Equal(2, catalog.All.Count);
    }

    [Fact]
    public void TryGet_reports_whether_driver_exists()
    {
        var catalog = new SimulatorCatalog();
        Assert.False(catalog.TryGet(7, out _));

        var created = catalog.Get(7);
        Assert.True(catalog.TryGet(7, out var found));
        Assert.Same(created, found);
    }
}

public class PlcDriverFactoryTests
{
    private static PlcConnection Connection(PlcBrand brand, int id = 1) => new() { Id = id, Name = $"PLC-{id}", Brand = brand };

    [Fact]
    public void Simulator_brand_resolves_from_catalog()
    {
        var catalog = new SimulatorCatalog();
        var factory = new PlcDriverFactory(catalog, []);

        var driver = factory.Create(Connection(PlcBrand.Simulator, 5));
        Assert.Same(catalog.Get(5), driver);
        Assert.Equal(PlcBrand.Simulator, driver.Brand);
    }

    [Fact]
    public void Unknown_brand_without_driver_package_throws_not_supported()
    {
        var factory = new PlcDriverFactory(new SimulatorCatalog(), []);
        var ex = Assert.Throws<NotSupportedException>(() => factory.Create(Connection(PlcBrand.ModbusTcp)));
        Assert.Contains("ModbusTcp", ex.Message);
    }

    [Fact]
    public void Physical_factory_is_used_for_matching_brand()
    {
        var stub = new StubPlcDriver(PlcBrand.SiemensS7);
        var physical = new StubPhysicalFactory([PlcBrand.SiemensS7], stub);
        var factory = new PlcDriverFactory(new SimulatorCatalog(), [physical]);

        Assert.Same(stub, factory.Create(Connection(PlcBrand.SiemensS7)));
        Assert.Throws<NotSupportedException>(() => factory.Create(Connection(PlcBrand.ModbusTcp)));
    }

    [Fact]
    public void First_matching_physical_factory_wins()
    {
        var first = new StubPlcDriver(PlcBrand.ModbusTcp);
        var second = new StubPlcDriver(PlcBrand.ModbusTcp);
        var factory = new PlcDriverFactory(
            new SimulatorCatalog(),
            [new StubPhysicalFactory([PlcBrand.ModbusTcp], first), new StubPhysicalFactory([PlcBrand.ModbusTcp], second)]);

        Assert.Same(first, factory.Create(Connection(PlcBrand.ModbusTcp)));
    }

    private sealed class StubPhysicalFactory(PlcBrand[] brands, IPlcDriver driver) : IPhysicalPlcDriverFactory
    {
        public bool CanCreate(PlcBrand brand) => brands.Contains(brand);

        public IPlcDriver Create(PlcConnection connection) => driver;
    }
}

/// <summary>真实驱动替换前的占位实现，仅用于验证工厂分发。</summary>
internal sealed class StubPlcDriver(PlcBrand brand) : IPlcDriver
{
    public PlcBrand Brand { get; } = brand;

    public PlcCapabilities Capabilities => PlcCapabilities.ModbusTcp;

    public bool IsConnected { get; private set; }

    public bool ConnectCalled { get; private set; }

    public int ReadCount { get; private set; }

    public bool TryParseAddress(string text, out PlcAddress address)
    {
        address = new PlcAddress("HOLDING", 0, -1, AddressKind.Word, text);
        return !string.IsNullOrWhiteSpace(text);
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectCalled = true;
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(Enumerable.Range(0, wordCount).Select(i => (ushort)(start.Offset + i)).ToArray());
    }

    public Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>单连接串行队列：自动重连、异常透传、取消与并发正确性。</summary>
public class PlcRequestQueueTests
{
    private static PlcAddress Addr(string text)
    {
        Assert.True(new InMemoryPlcDriver().TryParseAddress(text, out var address));
        return address;
    }

    [Fact]
    public async Task Queue_exposes_underlying_driver()
    {
        await using var driver = new InMemoryPlcDriver();
        await using var queue = new PlcRequestQueue(driver);
        Assert.Same(driver, queue.Driver);
    }

    [Fact]
    public async Task Queue_auto_connects_before_first_request()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.DisconnectAsync();
        Assert.False(driver.IsConnected);

        await using var queue = new PlcRequestQueue(driver, ownsDriver: false);
        driver.SetWord("D100", 42);

        // 上层从不显式连接，队列需在首个请求前补上连接。
        var words = await queue.ReadWordsAsync(Addr("D100"), 1);
        Assert.Equal((ushort)42, words[0]);
        Assert.True(driver.IsConnected);
    }

    [Fact]
    public async Task Queue_read_write_roundtrip()
    {
        await using var driver = new InMemoryPlcDriver();
        await using var queue = new PlcRequestQueue(driver);

        await queue.WriteWordsAsync(Addr("D500"), [7, 8, 9]);
        var words = await queue.ReadWordsAsync(Addr("D500"), 3);
        Assert.Equal(new ushort[] { 7, 8, 9 }, words);
    }

    [Fact]
    public async Task Queue_propagates_driver_failures_to_caller()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        await using var queue = new PlcRequestQueue(driver);

        driver.SetFailReads(true);
        await Assert.ThrowsAsync<PlcDriverException>(() => queue.ReadWordsAsync(Addr("D100"), 1));

        driver.SetFailWrites(true);
        await Assert.ThrowsAsync<PlcDriverException>(() => queue.WriteWordsAsync(Addr("D100"), [1]));
    }

    [Fact]
    public async Task Queue_honors_pre_canceled_token()
    {
        await using var driver = new InMemoryPlcDriver();
        await using var queue = new PlcRequestQueue(driver);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.ReadWordsAsync(Addr("D100"), 1, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.WriteWordsAsync(Addr("D100"), [1], cts.Token));
    }

    [Fact]
    public async Task Queue_keeps_serving_after_a_failed_request()
    {
        await using var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        await using var queue = new PlcRequestQueue(driver);

        driver.SetFailReads(true);
        await Assert.ThrowsAsync<PlcDriverException>(() => queue.ReadWordsAsync(Addr("D100"), 1));

        driver.SetFailReads(false);
        driver.SetWord("D100", 5);
        Assert.Equal((ushort)5, (await queue.ReadWordsAsync(Addr("D100"), 1))[0]);
    }

    [Fact]
    public async Task Queue_serializes_concurrent_requests_without_mixing_data()
    {
        await using var driver = new InMemoryPlcDriver(TimeSpan.FromMilliseconds(1));
        await driver.ConnectAsync();
        await using var queue = new PlcRequestQueue(driver);

        await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(i => queue.WriteWordsAsync(Addr($"D{2000 + i * 2}"), [(ushort)(i * 3 + 1)])));

        var results = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(i => queue.ReadWordsAsync(Addr($"D{2000 + i * 2}"), 1)));

        for (var i = 0; i < 32; i++)
        {
            Assert.Equal((ushort)(i * 3 + 1), results[i][0]);
        }
    }

    [Fact]
    public async Task Queue_rejects_requests_after_disposal()
    {
        var driver = new InMemoryPlcDriver();
        var queue = new PlcRequestQueue(driver);
        await queue.DisposeAsync();

        var error = await Record.ExceptionAsync(() => queue.ReadWordsAsync(Addr("D100"), 1));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Queue_owns_driver_when_requested()
    {
        var driver = new InMemoryPlcDriver();
        await driver.ConnectAsync();
        var queue = new PlcRequestQueue(driver, ownsDriver: true);
        await queue.DisposeAsync();

        // ownsDriver=true 时队列负责释放驱动，连接状态随之复位。
        Assert.False(driver.IsConnected);
    }
}
