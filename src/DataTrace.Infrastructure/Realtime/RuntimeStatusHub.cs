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
// 按职责拆成 partial 多文件：推送与快照本文件，连续 NG/预警见 .Streaks.cs，
// 在制会话与型号见 .Sessions.cs，状态比较见 .Equality.cs。
public sealed partial class RuntimeStatusHub : IRuntimeStatusHub, ICollectEventBus
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
}

