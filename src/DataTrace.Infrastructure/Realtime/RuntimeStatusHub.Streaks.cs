using DataTrace.Application.Alarms;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Realtime;

partial class RuntimeStatusHub
{
    private void NoteNgLocked(CollectRecord record)
        => NgStreakRebuild.Apply(_ngStreaks, record.StationId, record.StationCode, record.Judgement, record.ResultCode);

    private void NoteTagWatchLocked(CollectRecord record)
    {
        foreach (var tag in record.TagValues)
        {
            TagWatchRules.ApplyWarning(
                _warnings,
                record.StationId,
                record.StationCode,
                tag.TagId,
                tag.TagName,
                tag.IsWarning,
                tag.IsOutOfLimit);
            if (tag.NumericValue is not double value || double.IsNaN(value) || double.IsInfinity(value))
            {
                continue;
            }

            var key = (record.StationId, tag.TagId);
            if (!_driftSeries.TryGetValue(key, out var series))
            {
                series = [];
                _driftSeries[key] = series;
            }

            TagWatchRules.Append(series, value);
            var drift = TagWatchRules.DriftOf(record.StationId, record.StationCode, tag.TagId, tag.TagName, series);
            if (drift is null)
            {
                _drifts.Remove(key);
            }
            else
            {
                _drifts[key] = drift.Value;
            }
        }
    }

    public IReadOnlyList<StationNgStreak> NgStreaks
    {
        get
        {
            lock (_gate)
            {
                return _ngStreaks.Values.ToList();
            }
        }
    }

    public Task NgStreaksReady => _ngStreaksReady.Task;

    public bool TryClaimNgStreakRestore()
        => Interlocked.CompareExchange(ref _ngStreakRestoreClaimed, 1, 0) == 0;

    public void CompleteNgStreakRestore(IReadOnlyList<StationNgStreak> streaks)
    {
        lock (_gate)
        {
            _ngStreaks.Clear();
            foreach (var streak in streaks)
            {
                if (streak.Count > 0)
                {
                    _ngStreaks[(streak.StationId, streak.Kind)] = streak;
                }
            }
        }

        _ngStreaksReady.TrySetResult();
    }

    public void AbandonNgStreakRestore() => _ngStreaksReady.TrySetResult();

    public IReadOnlyList<TagWarningStreak> WarningStreaks
    {
        get
        {
            lock (_gate)
            {
                return _warnings.Values.ToList();
            }
        }
    }

    public IReadOnlyList<TagDriftNotice> DriftNotices
    {
        get
        {
            lock (_gate)
            {
                return _drifts.Values.ToList();
            }
        }
    }

    public void CompleteTagWatchRestore(IReadOnlyList<TagObservation> observations)
    {
        lock (_gate)
        {
            _warnings.Clear();
            _driftSeries.Clear();
            _drifts.Clear();
            foreach (var observation in observations.OrderBy(item => item.CompleteTime).ThenBy(item => item.TagId))
            {
                TagWatchRules.ApplyWarning(
                    _warnings,
                    observation.StationId,
                    observation.StationCode,
                    observation.TagId,
                    observation.TagName,
                    observation.IsWarning,
                    observation.IsOutOfLimit);
                if (observation.NumericValue is not double value || double.IsNaN(value) || double.IsInfinity(value))
                {
                    continue;
                }

                var key = (observation.StationId, observation.TagId);
                if (!_driftSeries.TryGetValue(key, out var series))
                {
                    series = [];
                    _driftSeries[key] = series;
                }

                TagWatchRules.Append(series, value);
                var drift = TagWatchRules.DriftOf(
                    observation.StationId,
                    observation.StationCode,
                    observation.TagId,
                    observation.TagName,
                    series);
                if (drift is null)
                {
                    _drifts.Remove(key);
                }
                else
                {
                    _drifts[key] = drift.Value;
                }
            }
        }
    }
}
