namespace DataTrace.Application.Mes;

public interface IMesPublisher
{
    Task EnqueueSessionAsync(string monthKey, long sessionId, string serialNo, string palletCode, string payloadJson, CancellationToken cancellationToken = default);
}
