namespace DataTrace.Application.Alarms;

/// <summary>一条尚未结束的报警：还没人接手，或者异常还在。</summary>
public sealed record AlarmIncidentDraft
{
    public long Id { get; init; }

    public required string Key { get; init; }

    public LineAlarmKind Kind { get; init; }

    public required string Message { get; init; }

    public DateTime RaisedAt { get; init; }

    public DateTime? AcknowledgedAt { get; init; }

    public string? AcknowledgedBy { get; init; }

    public DateTime? ClearedAt { get; init; }

    public bool NeedsOwner => AcknowledgedAt is null;

    public bool StillActive => ClearedAt is null;
}

/// <summary>接手结果。记录已经写下之后，审计失败单独带回。</summary>
public sealed class AlarmAckResult
{
    public bool Found { get; init; }

    public bool AlreadyTaken { get; init; }

    public string? TakenBy { get; init; }

    public string? AuditError { get; init; }
}

/// <summary>
/// 把这一轮仍在发生的异常并进尚未结束的记录。
/// 同一条 Key 只留一条：条件还在就更新说法；条件消失就记下解除时间，但仍等人接手。
/// 已经接手并且已经解除的不在这份名单里，下一轮会新开一条。
/// </summary>
public static class AlarmIncidentRules
{
    public static IReadOnlyList<AlarmIncidentDraft> Merge(
        IReadOnlyList<AlarmIncidentDraft> open,
        IReadOnlyList<LineAlarm> active,
        DateTime now)
    {
        var byKey = open
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.RaisedAt).ThenByDescending(item => item.Id).First(),
                StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AlarmIncidentDraft>();

        foreach (var alarm in active)
        {
            seen.Add(alarm.Key);
            if (byKey.TryGetValue(alarm.Key, out var existing))
            {
                result.Add(existing with
                {
                    Kind = alarm.Kind,
                    Message = alarm.Message,
                    ClearedAt = null
                });
            }
            else
            {
                result.Add(new AlarmIncidentDraft
                {
                    Key = alarm.Key,
                    Kind = alarm.Kind,
                    Message = alarm.Message,
                    RaisedAt = now
                });
            }
        }

        foreach (var existing in byKey.Values)
        {
            if (seen.Contains(existing.Key))
            {
                continue;
            }

            if (LineAlarmKeys.StaysUntilAcknowledged(existing.Key) && existing.NeedsOwner)
            {
                result.Add(existing with { ClearedAt = null });
                continue;
            }

            result.Add(existing.ClearedAt is null ? existing with { ClearedAt = now } : existing);
        }

        return result
            .OrderBy(item => item.NeedsOwner ? 0 : 1)
            .ThenBy(item => item.RaisedAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToList();
    }
}
