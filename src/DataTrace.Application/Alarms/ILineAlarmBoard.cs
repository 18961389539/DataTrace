namespace DataTrace.Application.Alarms;

/// <summary>当前正在呼叫的异常。后台写入，所有已打开的页面读取。</summary>
public interface ILineAlarmBoard
{
    LineAlarmSnapshot Current { get; }

    event Action? Changed;

    void Replace(LineAlarmSnapshot snapshot);
}
