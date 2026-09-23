namespace DataTrace.Plc.Abstractions;

public sealed class PlcCapabilities
{
    public required int MaxWordsPerRead { get; init; }
    public required int MaxWordsPerWrite { get; init; }
    public int MaxBitsPerRead { get; init; } = 256;
    public bool SupportsBitAddress { get; init; } = true;

    public static PlcCapabilities MitsubishiMc3E { get; } = new()
    {
        MaxWordsPerRead = 960,
        MaxWordsPerWrite = 960
    };

    public static PlcCapabilities SiemensS7 { get; } = new()
    {
        MaxWordsPerRead = 240,
        MaxWordsPerWrite = 240
    };

    public static PlcCapabilities ModbusTcp { get; } = new()
    {
        MaxWordsPerRead = 125,
        MaxWordsPerWrite = 123
    };

    public static PlcCapabilities OmronFins { get; } = new()
    {
        MaxWordsPerRead = 999,
        MaxWordsPerWrite = 999
    };

    public static PlcCapabilities Simulator { get; } = new()
    {
        MaxWordsPerRead = 4096,
        MaxWordsPerWrite = 4096
    };
}
