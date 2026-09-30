using DataTrace.Application.Alarms;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Realtime;

partial class RuntimeStatusHub
{
    private static bool SamePlc(PlcRuntimeStatus a, PlcRuntimeStatus b)
        => a.Name == b.Name
           && a.Connected == b.Connected
           && a.LastError == b.LastError;

    private static bool SameStation(StationRuntimeStatus a, StationRuntimeStatus b)
        => a.StationCode == b.StationCode
           && a.StationName == b.StationName
           && a.Sequence == b.Sequence
           && a.State == b.State
           && a.LastPalletCode == b.LastPalletCode
           && a.LastSerialNo == b.LastSerialNo
           && a.LastResultCode == b.LastResultCode
           && a.LastJudgement == b.LastJudgement
           && a.LastCompleteTime == b.LastCompleteTime
           && a.LastPieceGap == b.LastPieceGap
           && a.LastDurationMs == b.LastDurationMs
           && a.LastError == b.LastError
           && a.LastMonthKey == b.LastMonthKey
           && a.LastRecordId == b.LastRecordId
           && a.LastWriteBackAttempts == b.LastWriteBackAttempts
           && a.LastWriteBackOk == b.LastWriteBackOk
           && SameTags(a.LastTags, b.LastTags)
           && SameCurves(a.LastCurves, b.LastCurves);

    private static bool SameTags(IReadOnlyList<StationLiveTag> a, IReadOnlyList<StationLiveTag> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            var left = a[i];
            var right = b[i];
            if (left.Name != right.Name
                || left.Display != right.Display
                || left.Unit != right.Unit
                || left.NumericValue != right.NumericValue
                || left.LowerLimit != right.LowerLimit
                || left.UpperLimit != right.UpperLimit
                || left.WarningLowerLimit != right.WarningLowerLimit
                || left.WarningUpperLimit != right.WarningUpperLimit
                || left.OutOfLimit != right.OutOfLimit
                || left.Warning != right.Warning)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameCurves(IReadOnlyList<StationLiveCurve> a, IReadOnlyList<StationLiveCurve> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            var left = a[i];
            var right = b[i];
            if (left.Name != right.Name || !SameFloats(left.Values, right.Values))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameFloats(float[] a, float[] b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
