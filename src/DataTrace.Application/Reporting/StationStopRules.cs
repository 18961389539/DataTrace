using DataTrace.Domain;

namespace DataTrace.Application.Reporting;

/// <summary>某一站做完一件的时刻。顺序和是否末站由当前工站配置补上。</summary>
public sealed class StationPass
{
    public string MonthKey { get; init; } = "";
    public long SessionId { get; init; }
    public int StationId { get; init; }
    public string StationCode { get; init; } = "";
    public int Sequence { get; init; }
    public bool IsLastStation { get; init; }
    public DateTime CompleteTime { get; init; }
}

/// <summary>一个班次里，少做的件停在哪、停了多久。</summary>
public sealed class ShiftStopSummary
{
    public DateTime ShiftStart { get; init; }
    public bool HadPasses { get; init; }
    public bool WholeLine { get; init; }
    public string? StationCode { get; init; }
    public TimeSpan Duration { get; init; }
    public int LostPieces { get; init; }
    public int StopCount { get; init; }
    public int TotalLostPieces { get; init; }
    public string Detail { get; init; } = "";
    public string Brief { get; init; } = "—";
}

public sealed class ShiftStopReport
{
    public IReadOnlyList<ShiftStopSummary> ByShift { get; init; } = [];
}

/// <summary>
/// 从各站的完成时刻里找出停顿。短于一分钟、或不到平时节拍三倍的间隔当成抖动。
/// 某一站停着、别的站还在做完件，停点就是这一站；整线一起安静，停点是当时在制件停住的工站。
/// 少做件数用这段时间除以这一站平时做一件要多久。没有人确认。
/// </summary>
public static class StationStopRules
{
    public const int MinimumStopSeconds = 60;
    private static readonly TimeSpan MinimumStop = TimeSpan.FromSeconds(MinimumStopSeconds);
    private static readonly TimeSpan BeatSampleCap = TimeSpan.FromMinutes(5);

    public static ShiftStopReport Summarize(
        IReadOnlyList<StationPass> passes,
        DateTime from,
        DateTime to,
        DateTime asOf,
        int shiftStartHour,
        int shiftLengthHours)
    {
        var measuredTo = asOf < to ? asOf : to;
        if (measuredTo < from)
        {
            return new ShiftStopReport();
        }

        var ordered = passes
            .Where(pass => pass.CompleteTime <= measuredTo)
            .OrderBy(pass => pass.CompleteTime)
            .ThenBy(pass => pass.Sequence)
            .ToList();
        var beats = BeatTable.From(ordered);
        var locals = KeepEarliest(LocalStops(ordered, beats, measuredTo));
        var silences = Silences(ordered, beats, measuredTo)
            .Where(silence => !locals.Any(local => Overlaps(local, silence)))
            .ToList();
        var stops = locals.Concat(silences).ToList();

        ShiftWindow.Normalize(shiftStartHour, shiftLengthHours, out var hour, out var length);
        var rows = new List<ShiftStopSummary>();
        var cursor = ShiftWindow.Containing(from, hour, length).Start;
        while (cursor <= measuredTo)
        {
            var shiftEnd = cursor.AddHours(length);
            var clipFrom = cursor > from ? cursor : from;
            var clipTo = shiftEnd.AddTicks(-1);
            if (clipTo > measuredTo)
            {
                clipTo = measuredTo;
            }

            if (clipFrom <= clipTo)
            {
                var nearby = BeatTable.From(ordered.Where(pass =>
                    pass.CompleteTime >= cursor.AddHours(-1) && pass.CompleteTime <= clipTo).ToList());
                var clipped = new List<CountedStop>();
                foreach (var stop in stops)
                {
                    var start = stop.Start > clipFrom ? stop.Start : clipFrom;
                    var end = stop.End < clipTo.AddTicks(1) ? stop.End : clipTo.AddTicks(1);
                    var duration = end - start;
                    if (duration < MinimumStop)
                    {
                        continue;
                    }

                    clipped.Add(new CountedStop(stop, duration, Lost(duration, BeatFor(stop, nearby))));
                }

                var hadPasses = ordered.Any(pass => pass.CompleteTime >= clipFrom && pass.CompleteTime <= clipTo);
                rows.Add(Describe(cursor, hadPasses, clipped));
            }

            var next = shiftEnd;
            if (next <= cursor)
            {
                break;
            }

            cursor = next;
        }

        return new ShiftStopReport { ByShift = rows };
    }

    private static List<Idle> LocalStops(
        IReadOnlyList<StationPass> ordered,
        BeatTable beats,
        DateTime measuredTo)
    {
        var found = new List<Idle>();
        foreach (var group in ordered.GroupBy(pass => pass.StationId))
        {
            if (!beats.Stations.TryGetValue(group.Key, out var beat))
            {
                continue;
            }

            var mine = group.OrderBy(pass => pass.CompleteTime).ToList();
            var code = mine[0].StationCode;
            var sequence = mine[0].Sequence;
            for (var i = 0; i < mine.Count - 1; i++)
            {
                Consider(mine[i].CompleteTime, mine[i + 1].CompleteTime);
            }

            if (mine.Count > 0 && measuredTo > mine[^1].CompleteTime)
            {
                Consider(mine[^1].CompleteTime, measuredTo);
            }

            void Consider(DateTime prev, DateTime next)
            {
                var raw = next - prev;
                if (!IsNotable(raw, beat))
                {
                    return;
                }

                // 开头一两件是线上还没排空的件，不能当成「别的站还在做」。
                var stillRunning = ordered.Any(pass =>
                    pass.StationId != group.Key
                    && pass.CompleteTime >= prev + MinimumStop
                    && pass.CompleteTime < next);
                if (!stillRunning)
                {
                    return;
                }

                var start = prev + beat;
                if (next - start >= MinimumStop)
                {
                    found.Add(new Idle(group.Key, code, sequence, false, start, next));
                }
            }
        }

        return found;
    }

    private static List<Idle> Silences(
        IReadOnlyList<StationPass> ordered,
        BeatTable beats,
        DateTime measuredTo)
    {
        var found = new List<Idle>();
        if (ordered.Count == 0)
        {
            return found;
        }

        var lineKnown = beats.Line is not null;
        var lineBeat = beats.Line ?? TimeSpan.Zero;
        var times = ordered.Select(pass => pass.CompleteTime).Distinct().OrderBy(time => time).ToList();
        for (var i = 0; i < times.Count - 1; i++)
        {
            Consider(times[i], times[i + 1]);
        }

        if (measuredTo > times[^1])
        {
            Consider(times[^1], measuredTo);
        }

        return found;

        void Consider(DateTime prev, DateTime next)
        {
            var beat = lineKnown ? lineBeat : TimeSpan.Zero;
            if (!IsNotable(next - prev, lineKnown ? lineBeat : null))
            {
                return;
            }

            var start = lineKnown ? prev + lineBeat : prev;
            if (next - start < MinimumStop)
            {
                return;
            }

            var (stationId, code, sequence, wholeLine) = WherePiecesSat(ordered, prev);
            found.Add(new Idle(stationId, code, sequence, wholeLine, start, next));
        }
    }

    private static TimeSpan BeatFor(Idle stop, BeatTable beats)
    {
        if (!stop.WholeLine && beats.Stations.TryGetValue(stop.StationId, out var stationBeat))
        {
            return stationBeat;
        }

        return beats.Line ?? TimeSpan.Zero;
    }

    private static (int StationId, string? Code, int Sequence, bool WholeLine) WherePiecesSat(
        IReadOnlyList<StationPass> ordered,
        DateTime at)
    {
        var tally = new Dictionary<(int StationId, string Code, int Sequence), int>();
        foreach (var session in ordered.Where(pass => pass.CompleteTime <= at).GroupBy(pass => (pass.MonthKey, pass.SessionId)))
        {
            var reached = session.ToList();
            if (reached.Any(pass => pass.IsLastStation))
            {
                continue;
            }

            var seat = reached
                .OrderByDescending(pass => pass.Sequence)
                .ThenByDescending(pass => pass.CompleteTime)
                .First();
            var key = (seat.StationId, seat.StationCode, seat.Sequence);
            tally.TryGetValue(key, out var count);
            tally[key] = count + 1;
        }

        if (tally.Count == 0)
        {
            return (0, null, int.MaxValue, true);
        }

        var top = tally
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key.Sequence)
            .ThenBy(item => item.Key.Code, StringComparer.Ordinal)
            .First();
        return (top.Key.StationId, top.Key.Code, top.Key.Sequence, false);
    }

    private sealed class BeatTable
    {
        public TimeSpan? Line { get; init; }
        public Dictionary<int, TimeSpan> Stations { get; init; } = [];

        public static BeatTable From(IReadOnlyList<StationPass> ordered)
        {
            var stations = new Dictionary<int, TimeSpan>();
            foreach (var group in ordered.GroupBy(pass => pass.StationId))
            {
                var beat = ShortGaps(group.Select(pass => pass.CompleteTime));
                if (beat is { } known)
                {
                    stations[group.Key] = known;
                }
            }

            return new BeatTable
            {
                Line = ShortGaps(ordered.Select(pass => pass.CompleteTime)),
                Stations = stations
            };
        }
    }

    private static TimeSpan? ShortGaps(IEnumerable<DateTime> times)
    {
        var sorted = times.OrderBy(time => time).ToList();
        var gaps = new List<TimeSpan>();
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i] - sorted[i - 1];
            if (gap > TimeSpan.Zero && gap <= BeatSampleCap)
            {
                gaps.Add(gap);
            }
        }

        if (gaps.Count < 4)
        {
            return null;
        }

        gaps.Sort();
        return gaps[(int)Math.Floor((gaps.Count - 1) * 0.25)];
    }

    private static bool IsNotable(TimeSpan raw, TimeSpan? beat)
    {
        if (raw < MinimumStop)
        {
            return false;
        }

        if (beat is null || beat.Value <= TimeSpan.Zero)
        {
            return true;
        }

        var scaled = TimeSpan.FromTicks(beat.Value.Ticks * 3);
        return raw >= (scaled > MinimumStop ? scaled : MinimumStop);
    }

    private static List<Idle> KeepEarliest(List<Idle> stops)
    {
        var kept = new List<Idle>();
        foreach (var stop in stops.OrderBy(item => item.Start).ThenBy(item => item.Sequence).ThenBy(item => item.Code, StringComparer.Ordinal))
        {
            if (kept.Any(other => Overlaps(other, stop)))
            {
                continue;
            }

            kept.Add(stop);
        }

        return kept;
    }

    private static bool Overlaps(Idle left, Idle right)
        => left.Start < right.End && right.Start < left.End;

    private static int Lost(TimeSpan duration, TimeSpan beat)
        => beat <= TimeSpan.Zero ? 0 : (int)Math.Round(duration.TotalSeconds / beat.TotalSeconds, MidpointRounding.AwayFromZero);

    private static ShiftStopSummary Describe(DateTime shiftStart, bool hadPasses, List<CountedStop> stops)
    {
        if (stops.Count == 0)
        {
            return new ShiftStopSummary
            {
                ShiftStart = shiftStart,
                HadPasses = hadPasses,
                Detail = hadPasses ? "本班各站没有明显停顿" : "本班还没有采集",
                Brief = hadPasses ? "没有停顿" : "—"
            };
        }

        var top = stops
            .OrderByDescending(stop => stop.Duration)
            .ThenBy(stop => stop.Idle.Sequence)
            .ThenBy(stop => stop.Idle.Code, StringComparer.Ordinal)
            .First();
        var where = top.Idle.WholeLine ? "整线" : top.Idle.Code ?? "整线";
        var named = top.Idle.WholeLine ? where : " " + where;
        var duration = FormatDuration(top.Duration);
        var piece = top.Lost > 0 ? $"，约 {top.Lost} 件" : "";
        var briefPiece = top.Lost > 0 ? $" · 约 {top.Lost} 件" : "";
        var totalLost = stops.Sum(stop => stop.Lost);
        var detail = stops.Count == 1
            ? $"少做在{named}，停了 {duration}{piece}"
            : totalLost > 0
                ? $"少做最多在{named}，停了 {duration}{piece}；这一班合计少约 {totalLost} 件"
                : $"少做最多在{named}，停了 {duration}；这一班还有 {stops.Count - 1} 段停顿";
        var extra = stops.Count > 1 ? $" · 另 {stops.Count - 1} 段" : "";
        return new ShiftStopSummary
        {
            ShiftStart = shiftStart,
            HadPasses = hadPasses,
            WholeLine = top.Idle.WholeLine,
            StationCode = top.Idle.WholeLine ? null : top.Idle.Code,
            Duration = top.Duration,
            LostPieces = top.Lost,
            StopCount = stops.Count,
            TotalLostPieces = totalLost,
            Detail = detail,
            Brief = $"{where} · {duration}{briefPiece}{extra}"
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var minutes = (int)Math.Round(duration.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            minutes = 1;
        }

        if (minutes < 60)
        {
            return $"{minutes} 分钟";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return rest == 0 ? $"{hours} 小时" : $"{hours} 小时 {rest} 分钟";
    }

    private readonly record struct Idle(
        int StationId,
        string? Code,
        int Sequence,
        bool WholeLine,
        DateTime Start,
        DateTime End);

    private readonly record struct CountedStop(Idle Idle, TimeSpan Duration, int Lost);
}
