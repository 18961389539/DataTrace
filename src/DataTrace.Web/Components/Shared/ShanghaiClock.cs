namespace DataTrace.Web.Components.Shared;

/// <summary>
/// 看板「现在 / 今天」的唯一来源。
/// 今日件数按 Asia/Shanghai 切天，卡片上的相对时间如果各写各的 DateTime.Now，
/// 服务器不在上海时区时两者就会错开 —— 同一条记录可能既算进「今日」，
/// 卡片上又显示成「1 小时前」。
/// 注：采集器写入的 LastCompleteTime 是它本机的 DateTime.Now，
/// 跨机部署且时区不一致时要先统一那一侧，这里只保证 Web 端内部同源。
/// </summary>
public static class ShanghaiClock
{
    private static readonly TimeZoneInfo? Zone = Find();

    public static DateTime Now =>
        Zone is null ? DateTime.Now : TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    public static DateTime Today => Now.Date;

    private static TimeZoneInfo? Find()
    {
        try
        {
            var id = OperatingSystem.IsWindows() ? "China Standard Time" : "Asia/Shanghai";
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            // 容器里可能没有 tzdata；退回本地时钟，至少全站一致
            return null;
        }
    }
}
