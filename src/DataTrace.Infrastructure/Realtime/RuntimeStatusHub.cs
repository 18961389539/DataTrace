using DataTrace.Application.Realtime;
using DataTrace.Domain.Entities;

namespace DataTrace.Infrastructure.Realtime;

public sealed class RuntimeStatusHub : IRuntimeStatusHub, ICollectEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<int, StationRuntimeStatus> _stations = new();
    private readonly Dictionary<int, PlcRuntimeStatus> _plcs = new();
    private readonly List<CollectFeedItem> _recent = [];

    public event Action? Changed;
    public event Action<CollectRecord>? RecordSaved;

    public IReadOnlyList<StationRuntimeStatus> Stations
    {
        get
        {
            lock (_gate)
            {
                return _stations.Values.OrderBy(x => x.Sequence).ThenBy(x => x.StationCode).ToList();
            }
        }
    }

    public IReadOnlyList<PlcRuntimeStatus> Plcs
    {
        get
        {
            lock (_gate)
            {
                return _plcs.Values.OrderBy(x => x.Name).ToList();
            }
        }
    }

    public IReadOnlyList<CollectFeedItem> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToList();
            }
        }
    }

    public void UpsertStation(StationRuntimeStatus status)
    {
        lock (_gate)
        {
            _stations[status.StationId] = status;
        }

        Changed?.Invoke();
    }

    public void UpsertPlc(PlcRuntimeStatus status)
    {
        lock (_gate)
        {
            _plcs[status.PlcConnectionId] = status;
        }

        Changed?.Invoke();
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
        }

        RecordSaved?.Invoke(record);
        Changed?.Invoke();
    }
}
