using DataTrace.Application.Realtime;
using DataTrace.Domain.Entities;

namespace DataTrace.Infrastructure.Realtime;

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
    private DateTime _lastCollectorUtc = DateTime.UtcNow;

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


    public DateTime LastCollectorUtc
    {
        get { lock (_gate) { return _lastCollectorUtc; } }
    }

    public void NoteCollectorTick()
    {
        lock (_gate)
        {
            _lastCollectorUtc = DateTime.UtcNow;
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