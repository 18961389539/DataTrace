using System.Collections.Concurrent;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;

namespace DataTrace.Plc.Simulator;

public sealed class InMemoryPlcDriver : IPlcDriver
{
    private readonly MitsubishiAddressParser _parser = new();
    private readonly ConcurrentDictionary<(string Area, int Offset), ushort> _words = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _simulatedLatency;
    private volatile bool _connected;
    private volatile bool _failReads;
    private volatile bool _failWrites;

    public InMemoryPlcDriver(TimeSpan? simulatedLatency = null)
    {
        _simulatedLatency = simulatedLatency ?? TimeSpan.Zero;
    }

    public PlcBrand Brand => PlcBrand.Simulator;
    public PlcCapabilities Capabilities => PlcCapabilities.Simulator;
    public bool IsConnected => _connected;

    public TimeSpan SimulatedLatency => _simulatedLatency;

    public void SetFailReads(bool fail) => _failReads = fail;
    public void SetFailWrites(bool fail) => _failWrites = fail;

    public bool TryParseAddress(string text, out PlcAddress address) => _parser.TryParse(text, out address);

    public void SetWord(string address, ushort value)
    {
        if (!_parser.TryParse(address, out var parsed))
        {
            throw new ArgumentException($"非法地址 {address}");
        }

        _words[(parsed.Area, parsed.Offset)] = value;
    }

    public ushort GetWord(string address)
    {
        if (!_parser.TryParse(address, out var parsed))
        {
            throw new ArgumentException($"非法地址 {address}");
        }

        return _words.TryGetValue((parsed.Area, parsed.Offset), out var v) ? v : (ushort)0;
    }

    public void SetInt16(string address, short value) => SetWord(address, (ushort)value);

    public void SetFloat(string address, float value, FloatWordOrder order)
    {
        if (!_parser.TryParse(address, out var parsed))
        {
            throw new ArgumentException($"非法地址 {address}");
        }

        var words = ValueCodec.EncodeFloat(value, order);
        _words[(parsed.Area, parsed.Offset)] = words[0];
        _words[(parsed.Area, parsed.Offset + 1)] = words[1];
    }

    public void SetAscii(string address, string value, int length, bool highByteFirst = true)
    {
        if (!_parser.TryParse(address, out var parsed))
        {
            throw new ArgumentException($"非法地址 {address}");
        }

        var words = ValueCodec.EncodeAscii(value, length, highByteFirst);
        for (var i = 0; i < words.Length; i++)
        {
            _words[(parsed.Area, parsed.Offset + i)] = words[i];
        }
    }

    public void Trigger(string triggerAddress, short value = ResultCodesTrigger)
    {
        SetInt16(triggerAddress, value);
    }

    private const short ResultCodesTrigger = 1;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connected)
            {
                throw new PlcDriverException("模拟 PLC 未连接");
            }

            if (_failReads)
            {
                throw new PlcDriverException("模拟读取失败");
            }

            var data = new ushort[wordCount];
            for (var i = 0; i < wordCount; i++)
            {
                data[i] = _words.TryGetValue((start.Area, start.Offset + i), out var v) ? v : (ushort)0;
            }

            return data;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default)
    {
        await DelayAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_connected)
            {
                throw new PlcDriverException("模拟 PLC 未连接");
            }

            if (_failWrites)
            {
                throw new PlcDriverException("模拟写入失败");
            }

            for (var i = 0; i < words.Length; i++)
            {
                _words[(start.Area, start.Offset + i)] = words[i];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        if (_simulatedLatency > TimeSpan.Zero)
        {
            await Task.Delay(_simulatedLatency, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
