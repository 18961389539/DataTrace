using DataTrace.Collector;
using Microsoft.Extensions.Configuration;

namespace DataTrace.Tests;

public sealed class CollectorDiagnosticsMetricsTests
{
    [Fact]
    public void E2e_histogram_is_summarized_per_station_with_nearest_rank_percentiles()
    {
        // Access the meter before constructing listener, ensuring instrument publication is observed.
        _ = CollectMetrics.Meter;
        using var diagnostics = new CollectorDiagnostics();

        for (var ms = 1; ms <= 100; ms++) CollectMetrics.RecordE2E(ms, "ST01", 0);
        CollectMetrics.RecordE2E(900, "ST02", 0);

        var station = Assert.Single(diagnostics.RecentStationLatencies, x => x.StationCode == "ST01");
        Assert.Equal(100, station.SampleCount);
        Assert.Equal(95, station.P95Ms);
        Assert.Equal(99, station.P99Ms);
        Assert.Equal(900, Assert.Single(diagnostics.RecentStationLatencies, x => x.StationCode == "ST02").P95Ms);
    }

    [Fact]
    public void Active_collection_is_removed_when_ended()
    {
        using var diagnostics = new CollectorDiagnostics();
        var start = DateTime.Now.AddSeconds(-8);

        diagnostics.BeginCollection("ST01", start);
        Assert.Equal(start, Assert.Single(diagnostics.ActiveCollections).StartedAt);

        diagnostics.EndCollection("ST01");
        Assert.Empty(diagnostics.ActiveCollections);
    }

    [Fact]
    public void Collection_timeout_alarm_is_disabled_by_default_and_uses_configured_limit()
    {
        using var disabled = new CollectorDiagnostics();
        disabled.BeginCollection("ST01", DateTime.Now);
        Assert.Empty(disabled.OverdueCollections);
        Assert.Equal(0, disabled.CollectionMaxAgeSeconds);

        var settings = new Dictionary<string, string?> { ["DataTrace:Diagnostics:CollectionMaxAgeSeconds"] = "1" };
        using var enabled = new CollectorDiagnostics(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        enabled.BeginCollection("ST02", DateTime.Now);
        Thread.Sleep(1100);
        Assert.Contains("ST02", enabled.OverdueCollections.Select(x => x.StationCode));
    }

    [Fact]
    public void Active_collection_registry_has_a_fixed_capacity()
    {
        using var diagnostics = new CollectorDiagnostics();
        for (var index = 0; index < 140; index++) diagnostics.BeginCollection($"ST{index:D3}", DateTime.Now);

        Assert.Equal(128, diagnostics.ActiveCollections.Count);
    }
}
