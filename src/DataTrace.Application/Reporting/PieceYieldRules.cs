using DataTrace.Domain;
using DataTrace.Domain.Constants;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Reporting;

/// <summary>
/// 把已经走出末站的件收成班次和型号两张表，并找出拉低合格率最多的点。
/// 纯函数：一件在中途判过不合格，整件只算一次不合格，不再把后面工站的 OK 加进分母。
/// </summary>
public static class PieceYieldRules
{
    public static PieceYieldReport Summarize(
        IReadOnlyList<FinishedPieceObservation> pieces,
        int? stationId,
        int shiftStartHour,
        int shiftLengthHours)
    {
        var counted = new List<CountedPiece>();
        foreach (var piece in pieces)
        {
            if (stationId is int sid)
            {
                var atStation = piece.Stations.Where(station => station.StationId == sid).ToList();
                if (atStation.Count == 0)
                {
                    continue;
                }

                counted.Add(new CountedPiece(
                    piece,
                    OutcomeOf(atStation.Select(station => station.Judgement)),
                    piece.Faults.Where(fault => fault.StationId == sid).ToList()));
            }
            else
            {
                counted.Add(new CountedPiece(piece, piece.Judgement, piece.Faults));
            }
        }

        var byShift = counted
            .GroupBy(item => ShiftWindow.Containing(item.Piece.EndTime, shiftStartHour, shiftLengthHours).Start)
            .OrderBy(group => group.Key)
            .Select(group => Bucket(group.Key, group.Key.AddHours(ShiftWindowLength(shiftStartHour, shiftLengthHours)), group, stationId))
            .ToList();

        var byRecipe = counted
            .GroupBy(item => item.Piece.RecipeCode ?? "")
            .OrderBy(group => string.IsNullOrEmpty(group.Key) ? "~" : group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new RecipeThroughput
            {
                RecipeCode = group.Key,
                Total = group.Count(),
                Ok = group.Count(item => item.Outcome == Judgement.Ok),
                Ng = group.Count(item => item.Outcome == Judgement.Ng),
                Pending = group.Count(item => item.Outcome == Judgement.None)
            })
            .ToList();

        return new PieceYieldReport
        {
            ByShift = byShift,
            ByRecipe = byRecipe,
            Drag = DragOf(counted, stationId),
            FirstNg = FirstNgOf(counted)
        };
    }

    private static int ShiftWindowLength(int shiftStartHour, int shiftLengthHours)
    {
        ShiftWindow.Normalize(shiftStartHour, shiftLengthHours, out _, out var length);
        return length;
    }

    private static DailyThroughput Bucket(DateTime start, DateTime end, IEnumerable<CountedPiece> pieces, int? stationId)
    {
        var list = pieces.ToList();
        var quality = list.Count(item => IsQualityNg(item, stationId));
        var ng = list.Count(item => item.Outcome == Judgement.Ng);
        return new DailyThroughput
        {
            Day = start,
            ShiftLabel = new ShiftWindow(start, end).Label,
            Total = list.Count,
            Ok = list.Count(item => item.Outcome == Judgement.Ok),
            Ng = ng,
            QualityNg = quality,
            CollectNg = ng - quality,
            None = list.Count(item => item.Outcome == Judgement.None)
        };
    }

    /// <summary>
    /// 不合格件里，只要有一站是采成之后判废，就算质量不合格。
    /// 每一站的不合格都落在结果码 3–10 上，才算没采成。
    /// </summary>
    public static string NgSourceText(int quality, int collect)
    {
        if (quality == 0 && collect == 0)
        {
            return "本班没有不合格件";
        }

        if (collect == 0)
        {
            return $"质量不合格 {quality} 件";
        }

        if (quality == 0)
        {
            return $"没采成 {collect} 件";
        }

        return $"质量不合格 {quality} 件 · 没采成 {collect} 件";
    }

    private static bool IsQualityNg(CountedPiece piece, int? stationId)
    {
        if (piece.Outcome != Judgement.Ng)
        {
            return false;
        }

        var stations = piece.Piece.Stations.AsEnumerable();
        if (stationId is int sid)
        {
            stations = stations.Where(station => station.StationId == sid);
        }

        var failed = stations.Where(station => station.Judgement == Judgement.Ng).ToList();
        if (failed.Count == 0)
        {
            return false;
        }

        return failed.Any(station => !IsCollectFailure(station.ResultCode));
    }

    private static bool IsCollectFailure(short code)
        => code is >= ResultCodes.PlcReadFailed and <= ResultCodes.ArchiveFailed;

    private static PieceYieldDrag? DragOf(IReadOnlyList<CountedPiece> pieces, int? stationId)
    {
        var tally = new Dictionary<(string Station, string Name), (int Count, int High, int Low)>();
        foreach (var piece in pieces)
        {
            if (!IsQualityNg(piece, stationId))
            {
                continue;
            }

            // 没读到数不是越过红线。这种点不参加「哪一个点在拉低合格率」。
            var groups = piece.Faults
                .Where(fault => fault.Side != PointSide.Missing && !string.IsNullOrWhiteSpace(fault.Name))
                .GroupBy(fault => (Station: fault.StationCode.Trim(), Name: fault.Name.Trim()))
                .ToList();
            if (groups.Count == 0)
            {
                groups = piece.Piece.Stations
                    .Where(station => station.Judgement == Judgement.Ng && (stationId is null || station.StationId == stationId))
                    .Select(station => station.StationCode.Trim())
                    .Where(code => code.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .Select(code => new[] { new PieceFaultPoint(0, code, "不合格") }.GroupBy(_ => (Station: code, Name: "不合格")).First())
                    .ToList();
            }

            foreach (var group in groups)
            {
                tally.TryGetValue(group.Key, out var current);
                tally[group.Key] = (
                    current.Count + 1,
                    current.High + (group.Any(fault => fault.Side == PointSide.High) ? 1 : 0),
                    current.Low + (group.Any(fault => fault.Side == PointSide.Low) ? 1 : 0));
            }
        }

        if (tally.Count == 0)
        {
            return null;
        }

        var top = tally
            .OrderByDescending(item => item.Value.Count)
            .ThenBy(item => item.Key.Station, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Name, StringComparer.Ordinal)
            .First();
        return new PieceYieldDrag
        {
            StationCode = top.Key.Station,
            PointName = top.Key.Name,
            PieceCount = top.Value.Count,
            HighCount = top.Value.High,
            LowCount = top.Value.Low
        };
    }

    /// <summary>
    /// 每件只取工站顺序上第一次判废的那一站，再看哪一站这样出现得最多。
    /// 顺序用配置里的序号；没有序号时退回工站编号，避免全部挤成同一站。
    /// </summary>
    private static PieceFirstNg? FirstNgOf(IReadOnlyList<CountedPiece> pieces)
    {
        var tally = new Dictionary<string, (int Count, int Sequence, int StationId)>(StringComparer.Ordinal);
        foreach (var piece in pieces)
        {
            if (piece.Outcome != Judgement.Ng)
            {
                continue;
            }

            var first = piece.Piece.Stations
                .Where(station => station.Judgement == Judgement.Ng && !string.IsNullOrWhiteSpace(station.StationCode))
                .OrderBy(station => station.Sequence)
                .ThenBy(station => station.StationId)
                .Cast<PieceStationMark?>()
                .FirstOrDefault();
            if (first is not { } mark)
            {
                continue;
            }

            var code = mark.StationCode.Trim();
            tally.TryGetValue(code, out var current);
            var count = current.Count + 1;
            tally[code] = current.Count == 0 || mark.Sequence < current.Sequence || (mark.Sequence == current.Sequence && mark.StationId < current.StationId)
                ? (count, mark.Sequence, mark.StationId)
                : (count, current.Sequence, current.StationId);
        }

        if (tally.Count == 0)
        {
            return null;
        }

        var top = tally
            .OrderByDescending(item => item.Value.Count)
            .ThenBy(item => item.Value.Sequence)
            .ThenBy(item => item.Value.StationId)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .First();
        return new PieceFirstNg { StationCode = top.Key, PieceCount = top.Value.Count };
    }

    private static Judgement OutcomeOf(IEnumerable<Judgement> judgements)
    {
        var list = judgements.ToList();
        if (list.Count == 0 || list.All(judgement => judgement == Judgement.None))
        {
            return Judgement.None;
        }

        return list.Any(judgement => judgement == Judgement.Ng) ? Judgement.Ng : Judgement.Ok;
    }

    private readonly record struct CountedPiece(
        FinishedPieceObservation Piece,
        Judgement Outcome,
        IReadOnlyList<PieceFaultPoint> Faults);
}
