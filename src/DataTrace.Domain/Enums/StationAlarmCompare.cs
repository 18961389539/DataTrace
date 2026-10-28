namespace DataTrace.Domain.Enums;

/// <summary>自定义设备报警相对限值的比较方向。</summary>
public enum StationAlarmCompare
{
    /// <summary>当前值高于限值时触发。</summary>
    Above = 0,

    /// <summary>当前值低于限值时触发。</summary>
    Below = 1
}