using DataTrace.Domain.Constants;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Alarms;

/// <summary>当月一条采集记录的判定，只用于重算连续不合格。</summary>
public sealed class StationJudgementMark
{
    public int StationId { get; init; }

    public string StationCode { get; init; } = "";

    public DateTime CompleteTime { get; init; }

    public Judgement Judgement { get; init; }

    public short ResultCode { get; init; }
}

/// <summary>
/// 从当月记录重算连续不合格。合格把质量不合格和采集失败两串都清掉，未判定跳过。
/// 质量不合格（11）和采集失败或跳站（4、5、6、8）分开计；读失败、归档失败这类工站故障不计入，它们走工站故障报警。
/// 与采集发布时的计数同一套规则。
/// </summary>
public static class NgStreakRebuild
{
    public static IReadOnlyList<StationNgStreak> From(IEnumerable<StationJudgementMark> marks)
    {
        var streaks = new Dictionary<(int StationId, StationStreakKind Kind), StationNgStreak>();
        foreach (var mark in marks.OrderBy(m => m.CompleteTime).ThenBy(m => m.StationId))
        {
            Apply(streaks, mark.StationId, mark.StationCode, mark.Judgement, mark.ResultCode);
        }

        return streaks.Values.Where(item => item.Count > 0).ToList();
    }

    public static void Apply(
        IDictionary<(int StationId, StationStreakKind Kind), StationNgStreak> streaks,
        int stationId,
        string stationCode,
        Judgement judgement,
        short resultCode)
    {
        if (judgement == Judgement.None)
        {
            return;
        }

        var kind = KindOf(judgement, resultCode);
        if (kind is null)
        {
            Clear(streaks, stationId);
            return;
        }

        var code = string.IsNullOrWhiteSpace(stationCode) ? stationId.ToString() : stationCode;
        var key = (stationId, kind.Value);
        var count = streaks.TryGetValue(key, out var current) ? current.Count + 1 : 1;
        streaks[key] = new StationNgStreak(stationId, code, count, kind.Value);
        streaks.Remove((stationId, kind.Value == StationStreakKind.Quality ? StationStreakKind.Process : StationStreakKind.Quality));
    }

    /// <summary>不合格里该计入哪一串。null 表示这件把两串都打断，自己不计数。</summary>
    public static StationStreakKind? KindOf(Judgement judgement, short resultCode)
    {
        if (judgement != Judgement.Ng)
        {
            return null;
        }

        if (resultCode == ResultCodes.QualityRejected)
        {
            return StationStreakKind.Quality;
        }

        if (resultCode is ResultCodes.InvalidPalletCode
            or ResultCodes.DataValidationFailed
            or ResultCodes.DatabaseWriteFailed
            or ResultCodes.ProcessAbnormal)
        {
            return StationStreakKind.Process;
        }

        return null;
    }

    private static void Clear(
        IDictionary<(int StationId, StationStreakKind Kind), StationNgStreak> streaks,
        int stationId)
    {
        streaks.Remove((stationId, StationStreakKind.Quality));
        streaks.Remove((stationId, StationStreakKind.Process));
    }
}
