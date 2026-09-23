namespace DataTrace.Plc.Abstractions;

public sealed class PlcDriverException : Exception
{
    public PlcDriverException(string message) : base(message)
    {
    }

    public PlcDriverException(string message, Exception inner) : base(message, inner)
    {
    }
}
