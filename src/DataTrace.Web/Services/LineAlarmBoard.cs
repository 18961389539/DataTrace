using DataTrace.Application.Alarms;

namespace DataTrace.Web.Services;

public sealed class LineAlarmBoard : ILineAlarmBoard
{
    private readonly object _gate = new();
    private LineAlarmSnapshot _current = LineAlarmSnapshot.Empty;

    public event Action? Changed;

    public LineAlarmSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Replace(LineAlarmSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_current.Signature == snapshot.Signature
                && MessagesEqual(_current, snapshot))
            {
                return;
            }

            _current = snapshot;
        }

        Changed?.Invoke();
    }

    private static bool MessagesEqual(LineAlarmSnapshot left, LineAlarmSnapshot right)
    {
        if (left.Alarms.Count != right.Alarms.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Alarms.Count; i++)
        {
            if (left.Alarms[i].Message != right.Alarms[i].Message)
            {
                return false;
            }
        }

        return true;
    }
}
