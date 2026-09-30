namespace DataTrace.Application.Alarms;

public enum LineAlarmKind
{
    HeartbeatStale = 0,
    MesBacklog = 1,
    ConsecutiveNg = 2,
    StationFault = 3,
    SpoolBacklog = 4,
    ConsecutiveProcess = 5,
    SessionSuperseded = 6,
    ConsecutiveWarning = 7,
    ProcessDrift = 8,
    OpenSession = 9,

    /// <summary>数据盘剩余空间不足。不看采集开关：产线停下磁盘照样会被备份与归档吃掉。</summary>
    DiskLow = 10
}

/// <summary>同一工站上分开计的两串连续不合格。</summary>
public enum StationStreakKind
{
    /// <summary>质量不合格（结果码 11）。</summary>
    Quality = 0,

    /// <summary>采集没采成或跳站（结果码 4、5、6、8）。</summary>
    Process = 1
}

/// <summary>一条正在呼叫的异常。Key 在同一次异常持续期间保持不变。</summary>
public sealed record LineAlarm(string Key, LineAlarmKind Kind, string Message);

public sealed class LineAlarmSnapshot
{
    public static LineAlarmSnapshot Empty { get; } = new([]);

    public LineAlarmSnapshot(IReadOnlyList<LineAlarm> alarms)
    {
        Alarms = alarms;
    }

    public IReadOnlyList<LineAlarm> Alarms { get; }

    public string Signature => string.Join('|', Alarms.Select(a => a.Key));
}

/// <summary>某一工站当前连续不合格的件数。合格会把质量不合格和采集失败两串都清掉。</summary>
public readonly record struct StationNgStreak(
    int StationId,
    string StationCode,
    int Count,
    StationStreakKind Kind = StationStreakKind.Quality);

/// <summary>一块正在故障的工站。只描述这一轮要不要叫人。</summary>
public readonly record struct StationFaultNotice(int StationId, string StationCode, string Detail);

/// <summary>一次性事件的 Key。条件消失后仍留着等人接手，直到被接手才允许解除。</summary>
public static class LineAlarmKeys
{
    public const string SessionSupersededPrefix = "abandon:";

    public static string SessionSuperseded(long sessionId)
        => SessionSupersededPrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool StaysUntilAcknowledged(string key)
        => key.StartsWith(SessionSupersededPrefix, StringComparison.Ordinal);
}
