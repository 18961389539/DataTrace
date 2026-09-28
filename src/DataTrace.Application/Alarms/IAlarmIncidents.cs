namespace DataTrace.Application.Alarms;

/// <summary>报警的出现、解除和接手。页面与后台轮询共用这一份。</summary>
public interface IAlarmIncidents
{
    /// <summary>还没人接手，或者异常还没解除的记录。</summary>
    Task<IReadOnlyList<AlarmIncidentDraft>> ListAttentionAsync(CancellationToken cancellationToken = default);

    /// <summary>已经接手并且异常已经解除的记录，最近的在前。</summary>
    Task<IReadOnlyList<AlarmIncidentDraft>> ListRecentClosedAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记下一次已经发生的事件。不碰其他报警。
    /// 托盘被下一件盖掉就是这种：它不会在下一轮条件里再次出现，但要一直留到有人接手。
    /// </summary>
    Task RaiseAsync(LineAlarm alarm, DateTime now, CancellationToken cancellationToken = default);

    /// <summary>用这一轮仍在发生的异常更新记录，返回更新后仍需要关注的名单。</summary>
    Task<IReadOnlyList<AlarmIncidentDraft>> ApplyAsync(
        IReadOnlyList<LineAlarm> active,
        DateTime now,
        CancellationToken cancellationToken = default);

    /// <summary>记下是谁接手。已经接手的不再写第二次。</summary>
    Task<AlarmAckResult> AcknowledgeAsync(long id, string userName, CancellationToken cancellationToken = default);
}
