using DataTrace.Domain.Constants;
using DataTrace.Domain.Entities;

namespace DataTrace.Application.Alarms;

/// <summary>一件还没走到末站、而且已经待得太久的托盘。</summary>
public readonly record struct OpenSessionNotice(
    long SessionId,
    string PalletCode,
    string SerialNo,
    string StationCode,
    int OpenMinutes);

/// <summary>在制会话超过规定分钟还没关闭，就该叫人。纯函数。</summary>
public static class OpenSessionRules
{
    public static IReadOnlyList<OpenSessionNotice> Due(IEnumerable<ActiveSessionIndex> sessions, DateTime now)
    {
        var limit = TimeSpan.FromMinutes(SystemDefaults.OpenSessionWarnMinutes);
        return sessions
            .Where(session => now - session.StartTime >= limit)
            .OrderBy(session => session.StartTime)
            .ThenBy(session => session.PalletCode, StringComparer.Ordinal)
            .Select(session =>
            {
                var minutes = Math.Max(1, (int)(now - session.StartTime).TotalMinutes);
                var station = string.IsNullOrWhiteSpace(session.LastStationCode)
                    ? "尚未过站"
                    : session.LastStationCode.Trim();
                return new OpenSessionNotice(
                    session.SessionId,
                    session.PalletCode,
                    session.SerialNo,
                    station,
                    minutes);
            })
            .ToList();
    }

    public static string KeyOf(OpenSessionNotice notice)
        => notice.SessionId > 0
            ? "open:" + notice.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "open:" + notice.PalletCode;

    public static string MessageOf(OpenSessionNotice notice)
    {
        var serial = string.IsNullOrWhiteSpace(notice.SerialNo) ? "（无序列号）" : notice.SerialNo.Trim();
        return $"托盘 {notice.PalletCode}（{serial}）已在制 {notice.OpenMinutes} 分钟，停在 {notice.StationCode}，还没走到末站。";
    }
}
