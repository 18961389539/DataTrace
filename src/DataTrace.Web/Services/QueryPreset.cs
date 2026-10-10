using System.Globalization;
using DataTrace.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace DataTrace.Web.Services;

public sealed record QueryPreset(string Name, string Url, string Range)
{
    public static readonly (string Key, string Label)[] Ranges =
    [
        ("shift", "本班次"), ("previousShift", "上一班次"), ("today", "今天"),
        ("yesterday", "昨天"), ("week", "近 7 天"), ("month30", "近 30 天"),
        ("month", "本月"), ("fixed", "固定区间")
    ];

    public static (DateTime From, DateTime To) Dates(string range, DateTime now, int startHour, int lengthHours)
    {
        var shift = ShiftWindow.Containing(now, startHour, lengthHours);
        if (range == "previousShift") shift = ShiftWindow.Containing(shift.Start.AddTicks(-1), startHour, lengthHours);
        return range switch
        {
            "shift" or "previousShift" => (shift.Start, shift.End.AddTicks(-1)),
            "yesterday" => (now.Date.AddDays(-1), now.Date.AddDays(-1)),
            "week" => (now.Date.AddDays(-6), now.Date),
            "month30" => (now.Date.AddDays(-29), now.Date),
            "month" => (new DateTime(now.Year, now.Month, 1), now.Date),
            _ => (now.Date, now.Date)
        };
    }

    public static string DetectRange(DateTime? from, DateTime? to, DateTime now, int startHour, int lengthHours)
        => Ranges.Where(range => range.Key != "fixed")
            .FirstOrDefault(range => Dates(range.Key, now, startHour, lengthHours) == (from, to)).Key ?? "fixed";

    public string Resolve(DateTime now, int startHour, int lengthHours)
    {
        if (!RecordListNavigation.IsListUrl(Url) || !Ranges.Any(range => range.Key == Range))
            throw new ArgumentException("查询方案内容无效");
        var query = QueryHelpers.ParseQuery(Url.Contains('?') ? Url[Url.IndexOf('?')..] : "");
        query.Remove("page");
        query.Remove("days");
        if (Range != "fixed")
        {
            var dates = Dates(Range, now, startHour, lengthHours);
            query["from"] = dates.From.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            query["to"] = dates.To.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
        }
        return QueryHelpers.AddQueryString("query", query);
    }
}