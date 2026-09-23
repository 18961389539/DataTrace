namespace DataTrace.Domain.Entities;

public class SerialCounter
{
    public int Id { get; set; }
    public string DayKey { get; set; } = "";
    public int LastValue { get; set; }
}
