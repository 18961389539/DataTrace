using DataTrace.Application.Realtime;
using DataTrace.Domain.Enums;
using DataTrace.Infrastructure.Backup;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Realtime;
using DataTrace.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataTrace.Web.Tests;

public sealed class HealthProbeTests
{
    [Fact]
    public async Task Startup_readiness_blocks_health_even_when_collector_heartbeat_is_fresh()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datatrace-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "config.db")}");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        var status = new RuntimeStatusHub();
        status.SetCollectionReadiness(false, $"private path: {root}");
        status.NoteCollectorTick();
        var probe = new HealthProbe(
            new TestConfigContextFactory(options),
            new DataRootPaths(root),
            status,
            new TestHostEnvironment(),
            NullLogger<HealthProbe>.Instance);

        try
        {
            var report = await probe.CheckAsync();

            Assert.Equal("unhealthy", report.Status);
            var heartbeat = Assert.Single(report.Checks, check => check.Name == "collector");
            var readiness = Assert.Single(report.Checks, check => check.Name == "collection-readiness");
            Assert.True(heartbeat.Healthy);
            Assert.False(readiness.Healthy);
            Assert.Equal("启动核对未完成，采集已阻止", readiness.Detail);
            Assert.DoesNotContain(root, readiness.Detail, StringComparison.OrdinalIgnoreCase);

            status.SetCollectionReadiness(true);
            var readyReport = await probe.CheckAsync();
            Assert.True(Assert.Single(readyReport.Checks, check => check.Name == "collection-readiness").Healthy);
            Assert.True(Assert.Single(readyReport.Checks, check => check.Name == "collector").Healthy);
        }
        finally
        {
            await connection.CloseAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Faulted_station_blocks_health_without_exposing_station_details()
    {
        var root = Path.Combine(Path.GetTempPath(), $"datatrace-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "config.db")}");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite(connection).Options;
        var status = new RuntimeStatusHub();
        status.SetCollectionReadiness(true);
        status.NoteCollectorTick();
        status.UpsertStation(new StationRuntimeStatus
        {
            StationId = 1,
            StationName = "Sensitive station name",
            State = StationRuntimeState.Fault,
            LastError = $"private path: {root}"
        });
        status.UpsertStation(new StationRuntimeStatus { StationId = 2, State = StationRuntimeState.Idle });
        status.UpsertStation(new StationRuntimeStatus { StationId = 3, State = StationRuntimeState.Busy });
        status.UpsertStation(new StationRuntimeStatus { StationId = 4, State = StationRuntimeState.Disabled });
        var probe = new HealthProbe(
            new TestConfigContextFactory(options),
            new DataRootPaths(root),
            status,
            new TestHostEnvironment(),
            NullLogger<HealthProbe>.Instance);

        try
        {
            var report = await probe.CheckAsync();

            Assert.Equal("unhealthy", report.Status);
            Assert.True(Assert.Single(report.Checks, check => check.Name == "collector").Healthy);
            Assert.True(Assert.Single(report.Checks, check => check.Name == "collection-readiness").Healthy);
            var stationCheck = Assert.Single(report.Checks, check => check.Name == "collection-stations");
            Assert.False(stationCheck.Healthy);
            Assert.Equal("1 个工站处于故障状态", stationCheck.Detail);
            Assert.DoesNotContain("Sensitive station name", stationCheck.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(root, stationCheck.Detail, StringComparison.OrdinalIgnoreCase);

            status.UpsertStation(new StationRuntimeStatus { StationId = 1, State = StationRuntimeState.Idle });
            var recovered = await probe.CheckAsync();
            Assert.Equal("healthy", recovered.Status);
            Assert.True(Assert.Single(recovered.Checks, check => check.Name == "collection-stations").Healthy);
        }
        finally
        {
            await connection.CloseAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class TestConfigContextFactory(DbContextOptions<ConfigDbContext> options)
        : IDbContextFactory<ConfigDbContext>
    {
        public ConfigDbContext CreateDbContext() => new(options);

        public Task<ConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "DataTrace.Web.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
