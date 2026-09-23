using DataTrace.Domain.Enums;
using DataTrace.Plc.Addresses;

namespace DataTrace.Plc.Abstractions;

public interface IPlcDriver : IAsyncDisposable
{
    PlcBrand Brand { get; }
    PlcCapabilities Capabilities { get; }
    bool IsConnected { get; }

    bool TryParseAddress(string text, out PlcAddress address);
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<ushort[]> ReadWordsAsync(PlcAddress start, int wordCount, CancellationToken cancellationToken = default);
    Task WriteWordsAsync(PlcAddress start, ushort[] words, CancellationToken cancellationToken = default);
}

public interface IPlcDriverFactory
{
    IPlcDriver Create(Domain.Entities.PlcConnection connection);
}
