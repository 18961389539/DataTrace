using DataTrace.Domain.Entities;
using DataTrace.Domain.Enums;
using DataTrace.Plc.Codec;
using DataTrace.Plc.Simulator;

namespace DataTrace.Collector;

public sealed class SimulatedCycleOptions
{
    public Random Random { get; init; } = Random.Shared;
    public bool InjectNg { get; init; }
}

public static class SimulatorScenario
{
    public static void LoadStationCycle(
        InMemoryPlcDriver plc,
        PlcConnection connection,
        Station station,
        string palletCode,
        SimulatedCycleOptions? options = null)
    {
        options ??= new SimulatedCycleOptions();
        var random = options.Random;

        plc.SetAscii(station.PalletCodeAddress, palletCode, station.PalletCodeLength, connection.StringHighByteFirst);
        foreach (var pos in station.Positions)
        {
            if (!string.IsNullOrWhiteSpace(pos.OccupiedAddress))
            {
                plc.SetInt16(pos.OccupiedAddress!, 1);
            }
        }

        TagDefinition? ngTag = null;
        if (options.InjectNg)
        {
            ngTag = station.Tags
                .Where(t => t.Enabled && t.DataType != PlcDataType.String && (t.LowerLimit is not null || t.UpperLimit is not null))
                .OrderBy(_ => random.Next())
                .FirstOrDefault();
        }

        foreach (var tag in station.Tags.Where(t => t.Enabled))
        {
            if (tag.DataType == PlcDataType.String)
            {
                plc.SetAscii(tag.Address, tag == ngTag ? "NG" : "OK", Math.Max(2, tag.Length), connection.StringHighByteFirst);
                continue;
            }

            var value = tag == ngTag ? OutOfLimit(tag) : InRange(tag, random);
            WriteNumeric(plc, connection, tag, value);
        }

        foreach (var curve in station.Curves.Where(c => c.Enabled))
        {
            var phase = random.NextDouble() * Math.PI;
            var amp = 3.5 + random.NextDouble() * 2.5;
            foreach (var series in curve.Series)
            {
                for (var i = 0; i < curve.PointCount; i++)
                {
                    var ratio = curve.PointCount <= 1 ? 0 : (double)i / (curve.PointCount - 1);
                    var value = series.Role == SeriesRole.X
                        ? (float)(ratio * (4.5 + random.NextDouble()))
                        : (float)(7.5 + amp * Math.Sin(ratio * Math.PI + phase) + random.NextDouble() * 0.15);
                    var typeWords = ValueCodec.WordCountOf(series.DataType);
                    var stride = Math.Max(typeWords, series.StrideWords);
                    if (!plc.TryParseAddress(series.StartAddress, out var start))
                    {
                        continue;
                    }

                    var words = ValueCodec.EncodeFloat(value, connection.FloatWordOrder);
                    var offset = start.Offset + i * stride;
                    plc.SetWord($"{start.Area}{offset}", words[0]);
                    plc.SetWord($"{start.Area}{offset + 1}", words[1]);
                }
            }
        }

        plc.Trigger(station.TriggerAddress, station.TriggerValue);
    }

    private static void WriteNumeric(InMemoryPlcDriver plc, PlcConnection connection, TagDefinition tag, double value)
    {
        if (tag.DataType is PlcDataType.Float or PlcDataType.Double)
        {
            plc.SetFloat(tag.Address, (float)value, connection.FloatWordOrder);
        }
        else
        {
            plc.SetInt16(tag.Address, (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue));
        }
    }

    private static double InRange(TagDefinition tag, Random random)
    {
        var lo = tag.LowerLimit ?? 8;
        var hi = tag.UpperLimit ?? 16;
        if (hi <= lo)
        {
            hi = lo + 1;
        }

        var span = hi - lo;
        return lo + span * 0.2 + span * 0.6 * random.NextDouble();
    }

    private static double OutOfLimit(TagDefinition tag)
    {
        if (tag.UpperLimit is { } hi)
        {
            return hi + Math.Max(0.5, Math.Abs(hi) * 0.15);
        }

        if (tag.LowerLimit is { } lo)
        {
            return lo - Math.Max(0.5, Math.Abs(lo) * 0.15);
        }

        return 999;
    }
}
