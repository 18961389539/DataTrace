using DataTrace.Plc.Addresses;
using DataTrace.Plc.Codec;

namespace DataTrace.Tests;

public sealed class SeededDemoAddressTests
{
    [Fact]
    public async Task Demo_trigger_registers_do_not_overlap_curve_measurement_buffers()
    {
        await using var harness = await CollectHarness.CreateAsync();
        var parser = new MitsubishiAddressParser();
        foreach (var station in harness.Stations)
        {
            Assert.True(parser.TryParse(station.TriggerAddress, out var trigger));
            foreach (var curve in harness.Stations.SelectMany(x => x.Curves))
            foreach (var series in curve.Series)
            {
                Assert.True(parser.TryParse(series.StartAddress, out var start));
                var end = start.Offset + (curve.PointCount - 1) * series.StrideWords
                    + ValueCodec.WordCountOf(series.DataType, 0);
                Assert.False(trigger.Area == start.Area && trigger.Offset >= start.Offset && trigger.Offset < end,
                    $"{station.Code} trigger {station.TriggerAddress} overlaps curve {curve.Name}/{series.Name} at {series.StartAddress}");
            }
        }
    }
}
