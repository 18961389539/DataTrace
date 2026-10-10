namespace DataTrace.Application.Mes;

/// <summary>待推送的一条 MES 出站记录。不含持久化实体。</summary>
public sealed class MesOutboxPending
{
    public long Id { get; init; }
    public string? EventId { get; init; }
    public string SerialNo { get; init; } = "";
    public string MonthKey { get; init; } = "";
    public string PalletCode { get; init; } = "";
    public long PalletSessionId { get; init; }
    public string PayloadJson { get; init; } = "";
    public int AttemptCount { get; init; }
    public DateTime? LastAttemptAt { get; init; }
}

public sealed class MesOutboxMessage
{
    public required string EventId { get; init; }
    public required string MonthKey { get; init; }
    public required long PalletSessionId { get; init; }
    public required string SerialNo { get; init; }
    public required string PalletCode { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string PayloadJson { get; init; }
}

/// <summary>一次推送尝试的结果，由出站队列写回。</summary>
public sealed class MesOutboxAttempt
{
    public required long Id { get; init; }
    public required int AttemptCount { get; init; }
    public required DateTime AttemptedAt { get; init; }
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }
    public DateTime? NextAttemptAt { get; init; }
}

/// <summary>MES 出站队列。处理器不打开配置库。</summary>
public interface IMesOutboxQueue
{
    Task EnqueueIfMissingAsync(MesOutboxMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MesOutboxPending>> TakePendingAsync(int take, CancellationToken cancellationToken = default);
    Task SaveAttemptsAsync(IReadOnlyList<MesOutboxAttempt> attempts, CancellationToken cancellationToken = default);
}

public interface IMesOutboxRelay
{
    Task<int> RelayPendingAsync(int takePerMonth, CancellationToken cancellationToken = default);
}
