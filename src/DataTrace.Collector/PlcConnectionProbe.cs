using System.Globalization;
using System.Diagnostics;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;
using Microsoft.Extensions.Logging;

namespace DataTrace.Collector;

/// <summary>PLC 连接页的单地址读取测试结果。</summary>
/// <param name="UsedCollectorQueue">
/// true 表示走采集器已经持有的连接；false 表示临时连了一次；null 表示还没开始连。
/// </param>
public sealed record PlcProbeResult(
    DateTime At,
    long ElapsedMs,
    string Address,
    PlcDataType DataType,
    string RawText,
    string Display,
    bool? UsedCollectorQueue,
    string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// 对一条已保存的 PLC 连接做一次只读读取。
/// </summary>
/// <remarks>
/// 采集器已经占用这台 PLC 时必须复用它的队列：再开一条套接字会和扫描抢连接。
/// 没有队列（采集关闭、连接停用或尚未建连）时才临时连一次，读完即断开。
/// 模拟器驱动按连接 Id 共享，临时读取不能断开或释放它。
/// </remarks>
public interface IPlcConnectionProbe
{
    Task<PlcProbeResult> ReadAsync(
        PlcConnection connection,
        string? address,
        PlcDataType dataType,
        int stringLength,
        CancellationToken cancellationToken = default);
}

public sealed class PlcConnectionProbe : IPlcConnectionProbe
{
    /// <summary>PLC 未连接时会先重连（3 次 × 2 秒），必须有个头。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const int MaxStringChars = 64;

    private const int MaxRawWords = 8;

    private readonly IPlcQueueAccess _queues;
    private readonly IPlcDriverFactory _drivers;
    private readonly ILogger<PlcConnectionProbe> _logger;

    public PlcConnectionProbe(
        IPlcQueueAccess queues,
        IPlcDriverFactory drivers,
        ILogger<PlcConnectionProbe> logger)
    {
        _queues = queues;
        _drivers = drivers;
        _logger = logger;
    }

    public async Task<PlcProbeResult> ReadAsync(
        PlcConnection connection,
        string? address,
        PlcDataType dataType,
        int stringLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var at = DateTime.Now;
        var sw = Stopwatch.StartNew();
        var addressText = address?.Trim() ?? "";

        if (!PlcAddressValidator.TryValidate(connection.Brand, addressText, out _, out var invalid))
        {
            return Failed(at, sw, addressText, dataType, null, invalid ?? "地址无效");
        }

        var chars = dataType == PlcDataType.String ? Math.Clamp(stringLength, 1, MaxStringChars) : 0;
        var wordCount = ValueCodec.WordCountOf(dataType, chars);
        bool? usedQueue = null;
        using var timeout = new CancellationTokenSource(Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            ushort[] words;
            if (_queues.TryGetTrialQueue(connection.Id, out var queue, out _))
            {
                usedQueue = true;
                if (queue.IsCoolingDown)
                {
                    return Failed(at, sw, addressText, dataType, true, "这台 PLC 正在断线冷却，请稍后再试。");
                }

                if (!queue.Driver.TryParseAddress(addressText, out var parsed))
                {
                    return Failed(at, sw, addressText, dataType, true, "驱动无法解析该地址。");
                }

                words = await queue.ReadWordsAsync(parsed, wordCount, linked.Token).ConfigureAwait(false);
            }
            else
            {
                usedQueue = false;
                words = await ReadTemporaryAsync(connection, addressText, wordCount, linked.Token)
                    .ConfigureAwait(false);
            }

            if (words.Length < wordCount)
            {
                return Failed(at, sw, addressText, dataType, usedQueue, $"读回 {words.Length} 个字，需要 {wordCount} 个。");
            }

            return new PlcProbeResult(
                at,
                sw.ElapsedMilliseconds,
                addressText,
                dataType,
                FormatRaw(words),
                Format(words, dataType, connection, chars),
                usedQueue,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failed(at, sw, addressText, dataType, usedQueue, "读取超时（超过 10 秒）。");
        }
        catch (Exception ex) when (ex is PlcDriverException or InvalidOperationException or TimeoutException or IOException or NotSupportedException)
        {
            _logger.LogInformation(ex, "PLC 读取测试失败 {Connection} {Address}", connection.Name, addressText);
            return Failed(at, sw, addressText, dataType, usedQueue, ex.Message);
        }
    }

    /// <summary>
    /// 采集器没有这条连接的队列时，单独连一次。
    /// </summary>
    /// <remarks>
    /// 模拟器实例按连接 Id 放在目录里，采集器稍后还会拿同一个对象。
    /// 这里断开或释放它，会把目录里的那一份拆掉。
    /// </remarks>
    private async Task<ushort[]> ReadTemporaryAsync(
        PlcConnection connection,
        string addressText,
        int wordCount,
        CancellationToken cancellationToken)
    {
        var driver = _drivers.Create(connection);
        var shared = connection.Brand == PlcBrand.Simulator;
        try
        {
            await driver.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!driver.TryParseAddress(addressText, out var parsed) || parsed.IsBit)
            {
                throw new PlcDriverException("驱动无法解析该地址。");
            }

            return await driver.ReadWordsAsync(parsed, wordCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!shared)
            {
                try
                {
                    await driver.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取测试断开临时连接时失败 {Connection}", connection.Name);
                }

                await driver.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string Format(ushort[] words, PlcDataType dataType, PlcConnection connection, int stringLength)
    {
        if (dataType == PlcDataType.String)
        {
            return ValueCodec.DecodeAscii(words, stringLength, connection.StringHighByteFirst);
        }

        var number = ValueCodec.DecodeNumeric(words, dataType, connection.FloatWordOrder, 1, 0);
        if (dataType is PlcDataType.Bool or PlcDataType.Int16 or PlcDataType.Int32)
        {
            return ((long)number).ToString(CultureInfo.InvariantCulture);
        }

        return number.ToString("G", CultureInfo.InvariantCulture);
    }

    private static string FormatRaw(IReadOnlyList<ushort> words)
    {
        var shown = words.Take(MaxRawWords).Select(word => word.ToString("X4", CultureInfo.InvariantCulture));
        var text = string.Join(' ', shown);
        return words.Count > MaxRawWords ? text + " …" : text;
    }

    private static PlcProbeResult Failed(
        DateTime at,
        Stopwatch sw,
        string address,
        PlcDataType dataType,
        bool? usedQueue,
        string error)
        => new(at, sw.ElapsedMilliseconds, address, dataType, "", "", usedQueue, error);
}
