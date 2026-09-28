using System.Text.Encodings.Web;
using System.Text.Json;
using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;

namespace DataTrace.Application.Mes;

/// <summary>报文里的一个判废点：工站、点位、实测值和当时的规格限。</summary>
public sealed class MesRejectPoint
{
    public required string Station { get; init; }

    public required string Name { get; init; }

    public double? Value { get; init; }

    public double? Lower { get; init; }

    public double? Upper { get; init; }
}

/// <summary>末站发给 MES 的那一份 JSON。在原有字段上补上型号和判废点。</summary>
public static class MesPayload
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<MesRejectPoint> FromRecords(IEnumerable<CollectRecord> records)
    {
        var points = new List<MesRejectPoint>();
        foreach (var record in records)
        {
            foreach (var tag in record.TagValues.Where(item => item.IsOutOfLimit))
            {
                points.Add(new MesRejectPoint
                {
                    Station = record.StationCode,
                    Name = tag.TagName,
                    Value = tag.NumericValue,
                    Lower = tag.LowerLimit,
                    Upper = tag.UpperLimit
                });
            }

            foreach (var product in record.Products)
            {
                if (product.Judgement != Judgement.Ng || string.IsNullOrWhiteSpace(product.NgReason))
                {
                    continue;
                }

                if (product.NgReason.Contains("超限", StringComparison.Ordinal))
                {
                    continue;
                }

                points.Add(new MesRejectPoint
                {
                    Station = record.StationCode,
                    Name = product.NgReason
                });
            }
        }

        return points;
    }

    public static string Build(
        string serialNo,
        string palletCode,
        string station,
        string recipe,
        string judgement,
        DateTime time,
        IReadOnlyList<MesRejectPoint> rejects)
    {
        return JsonSerializer.Serialize(new
        {
            serialNo,
            palletCode,
            station,
            recipe,
            judgement,
            time,
            rejects = rejects.Select(item => new
            {
                station = item.Station,
                name = item.Name,
                value = item.Value,
                lower = item.Lower,
                upper = item.Upper
            })
        }, JsonOptions);
    }
}
