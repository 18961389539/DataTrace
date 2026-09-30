using DataTrace.Domain.Constants;

namespace DataTrace.Application.Alarms;

/// <summary>
/// 哪些异常要叫人。纯函数：页面、后台轮询和测试用同一份判断。
/// </summary>
public static class LineAlarmRules
{
    public static LineAlarmSnapshot Evaluate(
        bool collectEnabled,
        DateTime lastCollectorAt,
        bool mesEnabled,
        int mesPendingCount,
        DateTime? mesOldestPendingAt,
        IReadOnlyList<StationNgStreak> ngStreaks,
        DateTime now,
        IReadOnlyList<StationFaultNotice>? stationFaults = null,
        int spoolPendingCount = 0,
        DateTime? spoolOldestAt = null,
        IReadOnlyList<TagWarningStreak>? warningStreaks = null,
        IReadOnlyList<TagDriftNotice>? drifts = null,
        IReadOnlyList<OpenSessionNotice>? openSessions = null,
        long? diskFreeMegabytes = null)
    {
        var alarms = new List<LineAlarm>();

        if (collectEnabled && HeartbeatStale(lastCollectorAt, now))
        {
            alarms.Add(new LineAlarm(
                "heartbeat",
                LineAlarmKind.HeartbeatStale,
                $"采集心跳已停止，超过 {SystemDefaults.StaleHeartbeatSeconds} 秒没有刷新。"));
        }

        // 磁盘刻意不受 collectEnabled 约束：产线停下来检修时，备份、归档与日志照样在吃盘，
        // 而"停了就不叫"恰好会让它在最需要看着磁盘的时候安静。
        // 读不到剩余空间（null）不叫 —— 那是探活该报的"不知道"，不是这里该报的"确实不够"。
        if (diskFreeMegabytes is { } freeMegabytes && freeMegabytes < SystemDefaults.MinDiskFreeMegabytes)
        {
            alarms.Add(new LineAlarm(
                "disk",
                LineAlarmKind.DiskLow,
                $"数据盘只剩 {freeMegabytes} MB，低于 {SystemDefaults.MinDiskFreeMegabytes} MB；写满后采集会开始落库失败。"));
        }

        if (collectEnabled)
        {
            foreach (var fault in (stationFaults ?? []).OrderBy(f => f.StationCode, StringComparer.Ordinal).ThenBy(f => f.StationId))
            {
                var code = string.IsNullOrWhiteSpace(fault.StationCode) ? fault.StationId.ToString() : fault.StationCode;
                var detail = string.IsNullOrWhiteSpace(fault.Detail) ? "原因未记下" : fault.Detail.Trim();
                if (detail.Length > 80)
                {
                    detail = detail[..80];
                }

                alarms.Add(new LineAlarm(
                    $"fault:{fault.StationId}",
                    LineAlarmKind.StationFault,
                    $"工站 {code} 采集故障：{detail}"));
            }
        }

        if (mesEnabled
            && mesPendingCount > 0
            && mesOldestPendingAt is { } oldest
            && now - oldest > TimeSpan.FromHours(SystemDefaults.MesBacklogWarnHours))
        {
            var hours = Math.Max(1, (int)(now - oldest).TotalHours);
            alarms.Add(new LineAlarm(
                "mes",
                LineAlarmKind.MesBacklog,
                $"MES 有 {mesPendingCount} 条记录排队超过 {hours} 小时未推送成功。"));
        }

        if (spoolPendingCount > 0
            && spoolOldestAt is { } spoolOldest
            && now - spoolOldest >= TimeSpan.FromMinutes(SystemDefaults.SpoolBacklogWarnMinutes))
        {
            var minutes = Math.Max(1, (int)(now - spoolOldest).TotalMinutes);
            alarms.Add(new LineAlarm(
                "spool",
                LineAlarmKind.SpoolBacklog,
                $"有 {spoolPendingCount} 件采集记录写库失败，最早一笔已等待 {minutes} 分钟仍未补传入库。"));
        }

        foreach (var streak in ngStreaks
                     .Where(s => s.Count >= SystemDefaults.ConsecutiveNgAlarmCount)
                     .OrderBy(s => s.StationCode, StringComparer.Ordinal)
                     .ThenBy(s => s.Kind)
                     .ThenBy(s => s.StationId))
        {
            var code = string.IsNullOrWhiteSpace(streak.StationCode) ? streak.StationId.ToString() : streak.StationCode;
            if (streak.Kind == StationStreakKind.Process)
            {
                alarms.Add(new LineAlarm(
                    $"proc:{streak.StationId}",
                    LineAlarmKind.ConsecutiveProcess,
                    $"工站 {code} 连续 {streak.Count} 件采集失败或跳站。"));
            }
            else
            {
                alarms.Add(new LineAlarm(
                    $"ng:{streak.StationId}",
                    LineAlarmKind.ConsecutiveNg,
                    $"工站 {code} 连续 {streak.Count} 件质量不合格。"));
            }
        }

        foreach (var streak in (warningStreaks ?? [])
                     .Where(item => item.Count >= SystemDefaults.ConsecutiveNgAlarmCount)
                     .OrderBy(item => item.StationCode, StringComparer.Ordinal)
                     .ThenBy(item => item.TagName, StringComparer.Ordinal))
        {
            alarms.Add(new LineAlarm(
                $"warn:{streak.StationId}:{streak.TagId}",
                LineAlarmKind.ConsecutiveWarning,
                $"工站 {streak.StationCode} 点位 {streak.TagName} 连续 {streak.Count} 件落在预警带。"));
        }

        foreach (var drift in (drifts ?? [])
                     .OrderBy(item => item.StationCode, StringComparer.Ordinal)
                     .ThenBy(item => item.TagName, StringComparer.Ordinal))
        {
            alarms.Add(new LineAlarm(
                $"drift:{drift.StationId}:{drift.TagId}",
                LineAlarmKind.ProcessDrift,
                $"工站 {drift.StationCode} 点位 {drift.TagName} 过程漂移：{drift.Detail}"));
        }

        foreach (var session in openSessions ?? [])
        {
            alarms.Add(new LineAlarm(
                OpenSessionRules.KeyOf(session),
                LineAlarmKind.OpenSession,
                OpenSessionRules.MessageOf(session)));
        }

        return alarms.Count == 0 ? LineAlarmSnapshot.Empty : new LineAlarmSnapshot(alarms);
    }

    /// <summary>这一轮该再次通知的异常。新出现的立刻发；还没解除的按间隔重发。</summary>
    public static IReadOnlyList<LineAlarm> Due(
        IReadOnlyList<LineAlarm> active,
        IReadOnlyDictionary<string, DateTime> lastSent,
        DateTime now,
        TimeSpan repeat)
    {
        return active
            .Where(alarm => !lastSent.TryGetValue(alarm.Key, out var sent) || now - sent >= repeat)
            .ToList();
    }

    private static bool HeartbeatStale(DateTime lastCollectorAt, DateTime now)
        => lastCollectorAt == default || now - lastCollectorAt > TimeSpan.FromSeconds(SystemDefaults.StaleHeartbeatSeconds);
}
