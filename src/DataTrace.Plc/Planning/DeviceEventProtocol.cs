using DataTrace.Domain.Entities;
using DataTrace.Plc.Abstractions;
using DataTrace.Plc.Addresses;
using DataTrace.Plc.Queue;

namespace DataTrace.Plc.Planning;

/// <summary>
/// 可选 PLC 条件确认协议。安全性由 PLC 侧原子校验当前事件和幂等处理保证，不能用普通触发位模拟。
/// 固定低字在前的 UInt64 身份；零为未就绪，设备重启和托盘复用均不能复用身份。
/// </summary>
public static class DeviceEventProtocol
{
    public static bool Enabled(Station station) => !string.IsNullOrWhiteSpace(station.EventIdAddress)
        || !string.IsNullOrWhiteSpace(station.ConfirmationRequestAddress)
        || !string.IsNullOrWhiteSpace(station.ConfirmationAckAddress);

    public static string? ConfigurationError(Station station, Domain.Enums.PlcBrand brand)
    {
        if (!Enabled(station)) return null;
        var blocks = new List<(PlcAddress Address, int Words)>();
        foreach (var (text, length) in new[]
        {
            (station.EventIdAddress, 4), (station.ConfirmationRequestAddress, 5),
            (station.ConfirmationAckAddress, 5), (station.TriggerAddress, 1)
        })
        {
            if (!PlcAddressValidator.TryValidate(brand, text, out var address, out var error))
                return $"条件确认协议地址无效：{error}";
            var stride = address.OffsetUnit == OffsetUnit.Byte ? 2 : 1;
            if (blocks.Any(block => block.Address.Area == address.Area
                && block.Address.OffsetUnit == address.OffsetUnit
                && address.Offset < block.Address.Offset + block.Words * stride
                && block.Address.Offset < address.Offset + length * stride))
                return "事件号、确认请求、确认回执及触发寄存器不能相互重叠";
            blocks.Add((address, length));
        }
        return null;
    }

    public static long DecodeIdentity(IReadOnlyList<ushort> words)
    {
        if (words.Count < 4) throw new PlcDriverException("设备事件号响应不足4字");
        var value = (ulong)words[0] | ((ulong)words[1] << 16) | ((ulong)words[2] << 32) | ((ulong)words[3] << 48);
        if (value == 0) throw new PlcDriverException("设备事件号为0，PLC条件确认协议尚未就绪");
        return unchecked((long)value);
    }

    public static ushort[] Request(long eventId, short resultCode)
    {
        if (eventId == 0) throw new ArgumentOutOfRangeException(nameof(eventId));
        var value = unchecked((ulong)eventId);
        return [(ushort)value, (ushort)(value >> 16), (ushort)(value >> 32), (ushort)(value >> 48), unchecked((ushort)resultCode)];
    }

    private static PlcAddress Address(PlcRequestQueue queue, string text)
    {
        if (!queue.Driver.TryParseAddress(text, out var address) || address.IsBit)
            throw new PlcDriverException("条件确认协议需要有效字地址");
        return address;
    }

    public static async Task<long> ReadIdentityAsync(PlcRequestQueue queue, string address, CancellationToken token)
        => DecodeIdentity(await queue.ReadWordsAsync(Address(queue, address), 4, token).ConfigureAwait(false));

    /// <summary>只读持久确认块，检查 PLC 是否确认了指定事件号与结果码；绝不发起新的确认写入。</summary>
    public static async Task<bool> ReadAcknowledgedAsync(
        PlcRequestQueue queue, Station station, long eventId, short resultCode, CancellationToken token)
    {
        var ack = await queue.ReadWordsAsync(Address(queue, station.ConfirmationAckAddress), 5, token).ConfigureAwait(false);
        return ack.SequenceEqual(Request(eventId, resultCode));
    }

    /// <summary>先核对持久回执，再核对当前事件；只写独立请求块，绝不写普通触发寄存器。</summary>
    public static async Task<bool> ConfirmAsync(PlcRequestQueue queue, Station station, long eventId, short resultCode, CancellationToken token)
    {
        var request = Request(eventId, resultCode);
        var ackAddress = Address(queue, station.ConfirmationAckAddress);
        var ack = await queue.ReadWordsAsync(ackAddress, 5, token).ConfigureAwait(false);
        if (ack.SequenceEqual(request)) return true;
        if (await ReadIdentityAsync(queue, station.EventIdAddress, token).ConfigureAwait(false) != eventId)
            return false;
        await queue.WriteWordsAsync(Address(queue, station.ConfirmationRequestAddress), request, token).ConfigureAwait(false);
        // PLC 一个扫描周期后才可能产出回执。没有回执则保留留证，下次先核对，不猜测成功。
        ack = await queue.ReadWordsAsync(ackAddress, 5, token).ConfigureAwait(false);
        return ack.SequenceEqual(request);
    }
}
