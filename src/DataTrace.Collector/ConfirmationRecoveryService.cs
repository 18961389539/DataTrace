using DataTrace.Application.Configuration;
using DataTrace.Application.Identity;
using DataTrace.Application.Runtime;
using DataTrace.Application.Realtime;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Codec;

namespace DataTrace.Collector;

/// <summary>Operator recovery after the original PLC target and its current state have been verified.</summary>
public sealed class ConfirmationRecoveryService : IConfirmationRecoveryService
{
    // Recovery is scoped, but duplicate calls can originate from separate UI scopes.
    // Serialize every recovery handshake across scopes so two operators cannot race a PLC change.
    private static readonly SemaphoreSlim RecoveryGate = new(1, 1);
    private readonly IPendingWriteBackStore _pending;
    private readonly IConfigurationAuthorizer? _authorizer;
    private readonly IConfigRepository? _config;
    private readonly IPlcQueueAccess? _queues;
    private readonly IDataMaintenanceBarrier? _maintenance;
    private readonly IRuntimeStatusHub? _status;

    public ConfirmationRecoveryService(
        IPendingWriteBackStore pending,
        IConfigurationAuthorizer? authorizer = null,
        IConfigRepository? config = null,
        IPlcQueueAccess? queues = null,
        IDataMaintenanceBarrier? maintenance = null,
        IRuntimeStatusHub? status = null)
    {
        _pending = pending;
        _authorizer = authorizer;
        _config = config;
        _queues = queues;
        _maintenance = maintenance;
        _status = status;
    }

    public async Task<IReadOnlyList<PendingWriteBack>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_authorizer is null) return [];
        await _authorizer.AuthorizeCurrentRoleAsync([AppRoles.Administrator, AppRoles.Engineer], cancellationToken)
            .ConfigureAwait(false);
        return await _pending.ListManualReviewAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ManualConfirmationResolutionResult> ResolveAsync(
        string monthKey,
        long pendingId,
        string operatorName,
        string reason,
        string deviceStateEvidence,
        PendingConfirmationResolutionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operatorName) || string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(deviceStateEvidence))
            return Fail("操作人、核对说明和设备状态证据不能为空。");
        if (_authorizer is null || _config is null || _queues is null)
            return Fail("当前服务缺少最新身份或 PLC 状态验证能力。");

        await RecoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var recoveryLease = new SemaphoreRelease(RecoveryGate);

        // Revalidate after waiting: a role may have been revoked while another operator held the gate.
        try
        {
            await _authorizer.AuthorizeRoleAsync(operatorName.Trim(), [AppRoles.Administrator, AppRoles.Engineer], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ConfigurationAuthorizationException ex) { return Fail(ex.Message); }
        var actor = operatorName.Trim();

        await using var lease = _maintenance is null ? null : await _maintenance.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var pending = (await _pending.ListManualReviewAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => x.MonthKey == monthKey && x.Id == pendingId);
        if (pending is null) return Fail("待核对事件不存在或已处理。");
        if (pending.PlcConnectionId is null || string.IsNullOrWhiteSpace(pending.TriggerAddress)
            || pending.TriggerValue is null || string.IsNullOrWhiteSpace(pending.ConnectionFingerprint))
            return Fail("该旧事件未保存原 PLC 目标信息，不能自动解除；请现场核对原设备并升级留证。");

        var snapshot = await _config.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var station = snapshot.Stations.FirstOrDefault(x => x.Id == pending.StationId);
        var connection = snapshot.PlcConnections.FirstOrDefault(x => x.Id == pending.PlcConnectionId);
        if (station is null || connection is null || station.PlcConnectionId != pending.PlcConnectionId
            || station.TriggerAddress != pending.TriggerAddress || station.TriggerValue != pending.TriggerValue
            || ConfirmationTargetFingerprint.Of(connection) != pending.ConnectionFingerprint)
            return Fail("当前 PLC 连接或触发地址已变化；为避免向新设备写旧结果，保持人工阻塞。");
        if (DataTrace.Plc.Planning.DeviceEventProtocol.Enabled(station)
            && (pending.DeviceEventId is null || station.EventIdAddress != pending.EventIdAddress
                || station.ConfirmationRequestAddress != pending.ConfirmationRequestAddress
                || station.ConfirmationAckAddress != pending.ConfirmationAckAddress))
            return Fail("条件确认协议与原留证不一致，保持人工阻塞。");
        if (!_queues.TryGetTrialQueue(connection.Id, out var queue, out var unavailable))
            return Fail(unavailable ?? "PLC 队列不可用。");
        if (!queue.Driver.TryParseAddress(pending.TriggerAddress, out var address) || address.IsBit)
            return Fail("原触发地址不可读，保持人工阻塞。");

        try
        {
            var words = await queue.ReadWordsAsync(address, 1, cancellationToken).ConfigureAwait(false);
            if (words.Length != 1) return Fail("原触发地址未返回完整数据，保持人工阻塞。");
            var current = unchecked((short)words[0]);
            if (mode == PendingConfirmationResolutionMode.DeviceAlreadyAcknowledged)
            {
                if (current == pending.TriggerValue) return Fail("触发位仍为触发值；不能按设备已确认解除。");
                if (pending.DeviceEventId is { } eventId
                    && (!DataTrace.Plc.Planning.DeviceEventProtocol.Enabled(station)
                        || DataTrace.Plc.Planning.DeviceEventProtocol.ConfigurationError(station, connection.Brand) is not null))
                    return Fail("条件确认协议与原留证不一致，保持人工阻塞。");
                if (pending.DeviceEventId is { } acknowledgedEventId
                    && !await DataTrace.Plc.Planning.DeviceEventProtocol.ReadAcknowledgedAsync(
                        queue, station, acknowledgedEventId, pending.ResultCode, cancellationToken).ConfigureAwait(false))
                    return Fail("PLC持久确认回执与原事件号或结果码不匹配；保持人工阻塞。");
            }
            else if (mode == PendingConfirmationResolutionMode.SameEventConfirmed)
            {
                // This mode relies on the operator's explicit physical identity attestation.
                if (current != pending.TriggerValue) return Fail("触发位已变化；原事件身份无法从 PLC 值确认，保持人工阻塞。");
                if (pending.DeviceEventId is { } eventId)
                {
                    if (!DataTrace.Plc.Planning.DeviceEventProtocol.Enabled(station)
                        || !await DataTrace.Plc.Planning.DeviceEventProtocol.ConfirmAsync(queue, station, eventId, pending.ResultCode, cancellationToken).ConfigureAwait(false))
                        return Fail("PLC条件确认回执与原事件不匹配，保持人工阻塞。");
                }
                else
                {
                    await queue.WriteWordsAsync(address, ValueCodec.EncodeInt16(pending.ResultCode), cancellationToken).ConfigureAwait(false);
                    var readback = await queue.ReadWordsAsync(address, 1, cancellationToken).ConfigureAwait(false);
                    if (readback.Length != 1 || unchecked((short)readback[0]) != pending.ResultCode)
                        return Fail("回写读回不匹配，保持人工阻塞。");
                }
            }
            else return Fail("未知核对模式，保持人工阻塞。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail($"PLC 核对失败，保持人工阻塞：{ex.Message}");
        }

        var resolved = await _pending.ResolveManualReviewAsync(
            monthKey, pendingId, actor,
            $"{mode}: {reason.Trim()}", deviceStateEvidence.Trim(), DateTime.Now, cancellationToken)
            .ConfigureAwait(false);
        if (!resolved) return Fail("留证状态已变化，请刷新后再试。");
        if (_status is not null)
        {
            var previous = _status.Stations.FirstOrDefault(x => x.StationId == pending.StationId);
            if (previous is not null)
            {
                _status.UpsertStation(new StationRuntimeStatus
                {
                    StationId = previous.StationId, StationCode = previous.StationCode, StationName = previous.StationName,
                    Sequence = previous.Sequence, State = StationRuntimeState.Idle,
                    LastPalletCode = previous.LastPalletCode, LastSerialNo = previous.LastSerialNo,
                    LastResultCode = previous.LastResultCode, LastJudgement = previous.LastJudgement,
                    LastCompleteTime = previous.LastCompleteTime, LastPieceGap = previous.LastPieceGap,
                    LastDurationMs = previous.LastDurationMs, LastError = null, LastMonthKey = previous.LastMonthKey,
                    LastRecordId = previous.LastRecordId, LastWriteBackAttempts = previous.LastWriteBackAttempts,
                    LastWriteBackOk = mode == PendingConfirmationResolutionMode.SameEventConfirmed,
                    LastTags = previous.LastTags, LastCurves = previous.LastCurves
                });
            }
        }
        return new ManualConfirmationResolutionResult(true);
    }

    private static ManualConfirmationResolutionResult Fail(string message) => new(false, message);

    private sealed class SemaphoreRelease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
