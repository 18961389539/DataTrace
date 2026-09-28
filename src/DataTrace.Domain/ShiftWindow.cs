using DataTrace.Domain.Constants;

namespace DataTrace.Domain;

/// <summary>
/// 一个班次的时间窗。起点是整点，时长只能是 8、12 或 24 小时，各班首尾相接。
/// 夜班可以跨过零点，仍然算同一班。
/// </summary>
public readonly record struct ShiftWindow(DateTime Start, DateTime End)
{
    /// <summary>非法配置退回默认班次，避免看板和报表因为一个坏数字停下来。</summary>
    public static void Normalize(int startHour, int lengthHours, out int hour, out int length)
    {
        if (startHour is >= 0 and <= 23 && lengthHours is 8 or 12 or 24)
        {
            hour = startHour;
            length = lengthHours;
            return;
        }

        hour = SystemDefaults.ShiftStartHour;
        length = SystemDefaults.ShiftLengthHours;
    }

    /// <summary>包含 <paramref name="time"/> 的那一班。结束时刻不含在本班内。</summary>
    public static ShiftWindow Containing(DateTime time, int startHour, int lengthHours)
    {
        Normalize(startHour, lengthHours, out startHour, out lengthHours);
        var anchor = time.Date.AddHours(startHour);
        if (time < anchor)
        {
            anchor = anchor.AddDays(-1);
        }

        var steps = (int)Math.Floor((time - anchor).TotalHours / lengthHours);
        var start = anchor.AddHours(steps * lengthHours);
        return new ShiftWindow(start, start.AddHours(lengthHours));
    }

    /// <summary>同一天写成 09-28 08:00–20:00；跨零点写成 09-28 20:00–09-29 08:00。</summary>
    public string Label
    {
        get
        {
            var last = End.AddTicks(-1);
            return Start.Date == last.Date
                ? $"{Start:MM-dd HH:mm}–{End:HH:mm}"
                : $"{Start:MM-dd HH:mm}–{End:MM-dd HH:mm}";
        }
    }
}
