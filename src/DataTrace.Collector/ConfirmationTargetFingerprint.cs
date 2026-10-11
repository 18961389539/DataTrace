using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataTrace.Domain.Entities;

namespace DataTrace.Collector;

internal static class ConfirmationTargetFingerprint
{
    public static string Of(PlcConnection connection)
    {
        // Hash only stable connection settings; skip Stations navigation to avoid EF cycles.
        var canonical = new
        {
            connection.Id, connection.Name, connection.Brand, connection.Host, connection.Port,
            connection.TimeoutMs, connection.FloatWordOrder, connection.StringHighByteFirst,
            connection.MergeGapWords, connection.Enabled, connection.Extra
        };
        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
