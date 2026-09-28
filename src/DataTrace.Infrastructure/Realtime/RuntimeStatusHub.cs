using DataTrace.Application.Alarms;
using DataTrace.Application.Realtime;
using DataTrace.Application.Runtime;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Infrastructure.Realtime;

/// <summary>
/// 工站状态、最近记录、连续不合格和心跳只活在本进程。
/// 连续不合格在启动时从当月库重算；最近记录和心跳从这次进程开始算。
/// </summary>
public sealed class RuntimeStatusHub : IRuntimeStatusHub, ICollectEventBus
{
    private static readonly TimeSpan ChangedCoalesce = TimeSpan.FromMilliseconds(75);

    private readonly object _gate = new();
    private readonly Dictionary<int, StationRuntimeStatus> _stations = new();
    private readonly Dictionary<int, PlcRuntimeStatus> _plcs = new();
    private readonly List<CollectFeedItem> _recent = [];

    private IReadOnlyList<StationRuntimeStatus> _stationsSnapshot = [];
    private IReadOnlyList<PlcRuntimeStatus> _plcsSnapshot = [];
    private IReadOnlyList<CollectFeedItem> _recentSnapshot = [];
    private string? _activeRecipeCode;
    private string? _activeRecipeName;
    private readonly Dictionary<(int StationId, StationStreakKind Kind), StationNgStreak> _ngStreaks = new();
    private readonly Dictionary<(int StationId, int TagId), TagWarningStreak> _warnings = new();
    private readonly Dictionary<(int StationId, int TagId), List<double>> _driftSeries = new();
    private readonly Dictionary<(int StationId, int TagId), TagDriftNotice> _drifts = new();
    private IReadOnlyList<OpenSessionNotice> _openSessions = [];
    private IReadOnlyList<InProcessRecipe> _inProcessRecipes = [];
    private readonly TaskCompletionSource _ngStreaksReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _ngStreakRestoreClaimed;
    private DateTime _lastCollectorAt = DateTime.Now;

    private readonly object _notifyGate = new();
    private bool _notifyPending;

    public event Action? Changed;
    public event Action<CollectRecord>? RecordSaved;

    public IReadOnlyList<StationRuntimeStatus> Stations
    {
        get
        {
            lock (_gate)
            {
                return _stationsSnapshot;
            }
        }
    }

    public IReadOnlyList<PlcRuntimeStatus> Plcs
    {
        get
        {
            lock (_gate)
            {
                return _plcsSnapshot;
            }
        }
    }

    public IReadOnlyList<CollectFeedItem> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recentSnapshot;
            }
        }
    }
    public string? ActiveRecipeCode
    {
        get { lock (_gate) { return _activeRecipeCode; } }
    }

    public string? ActiveRecipeName
    {
        get { lock (_gate) { return _activeRecipeName; } }
    }


    public void UpsertStation(StationRuntimeStatus status)
    {
        lock (_gate)
        {
            if (_stations.TryGetValue(status.StationId, out var existing) && SameStation(existing, status))
            {
                return;
            }

            _stations[status.StationId] = status;
            _stationsSnapshot = _stations.Values.OrderBy(x => x.Sequence).ThenBy(x => x.StationCode).ToList();
        }

        ScheduleChanged();
    }

    public void UpsertPlc(PlcRuntimeStatus status)
    {
        lock (_gate)
        {
            if (_plcs.TryGetValue(status.PlcConnectionId, out var existing) && SamePlc(existing, status))
            {
                return;
            }

            _plcs[status.PlcConnectionId] = status;
            _plcsSnapshot = _plcs.Values.OrderBy(x => x.Name).ToList();
        }

        ScheduleChanged();
    }

    public void Publish(CollectRecord record)
    {
        var item = new CollectFeedItem
        {
            MonthKey = record.TriggerTime.ToString("yyyyMM"),
            RecordId = record.Id,
            StationCode = record.StationCode,
            PalletCode = record.PalletCode,
            SerialNo = record.SerialNo,
            RecipeCode = record.RecipeCode ?? "",
            ResultCode = record.ResultCode,
            Judgement = record.Judgement,
            CompleteTime = record.CompleteTime == default ? record.TriggerTime : record.CompleteTime,
            DurationMs = record.DurationMs,
            Error = record.ErrorMessage
        };

        lock (_gate)
        {
            NoteNgLocked(record);
            NoteTagWatchLocked(record);
            _recent.Insert(0, item);
            if (_recent.Count > 40)
            {
                _recent.RemoveRange(40, _recent.Count - 40);
            }

            _recentSnapshot = _recent.ToList();
        }

        RecordSaved?.Invoke(record);
        ScheduleChanged();
    }

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

    public void SetActiveRecipe(string? code, string? name)
    {
        var normalizedCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        var normalizedName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        lock (_gate)
        {
            if (string.Equals(_activeRecipeCode, normalizedCode, StringComparison.Ordinal)
                && string.Equals(_activeRecipeName, normalizedName, StringComparison.Ordinal))
            {
                return;
            }

            _activeRecipeCode = normalizedCode;
            _activeRecipeName = normalizedName;
        }

        ScheduleChanged();
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

    public IReadOnlyList<OpenSessionNotice> OpenSessions
    {
        get
        {
            lock (_gate)
            {
                return _openSessions;
            }
        }
    }

    public void ReplaceOpenSessions(IReadOnlyList<OpenSessionNotice> sessions)
    {
        var next = sessions.ToList();
        lock (_gate)
        {
            if (SameOpen(_openSessions, next))
            {
                return;
            }

            _openSessions = next;
        }

        ScheduleChanged();
    }

    public IReadOnlyList<InProcessRecipe> InProcessRecipes
    {
        get
        {
            lock (_gate)
            {
                return _inProcessRecipes;
            }
        }
    }

    public void ReplaceInProcessRecipes(IReadOnlyList<InProcessRecipe> recipes)
    {
        var next = recipes.ToList();
        lock (_gate)
        {
            if (SameRecipes(_inProcessRecipes, next))
            {
                return;
            }

            _inProcessRecipes = next;
        }

        ScheduleChanged();
    }

    private static bool SameRecipes(IReadOnlyList<InProcessRecipe> left, IReadOnlyList<InProcessRecipe> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Code, right[i].Code, StringComparison.Ordinal) || left[i].Count != right[i].Count)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameOpen(IReadOnlyList<OpenSessionNotice> left, IReadOnlyList<OpenSessionNotice> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].SessionId != right[i].SessionId
                || left[i].PalletCode != right[i].PalletCode
                || left[i].StationCode != right[i].StationCode
                || left[i].OpenMinutes != right[i].OpenMinutes)
            {
                return false;
            }
        }

        return true;
    }

    public DateTime LastCollectorAt
    {
        get { lock (_gate) { return _lastCollectorAt; } }
    }

    public void NoteCollectorTick()
    {
        lock (_gate)
        {
            _lastCollectorAt = DateTime.Now;
        }
    }

    public void NotifyChanged() => ScheduleChanged();

    private void ScheduleChanged()
    {
        lock (_notifyGate)
        {
            if (_notifyPending)
            {
                return;
            }

            _notifyPending = true;
        }

        _ = EmitChangedAsync();
    }

    private async Task EmitChangedAsync()
    {
        try
        {
            await Task.Delay(ChangedCoalesce).ConfigureAwait(false);
        }
        catch
        {
            lock (_notifyGate)
            {
                _notifyPending = false;
            }

            return;
        }

        lock (_notifyGate)
        {
            _notifyPending = false;
        }

        Changed?.Invoke();
    }

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
