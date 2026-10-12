using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
using DataTrace.Infrastructure.Backup;
using DataTrace.Infrastructure.Persistence;
using DataTrace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;

namespace DataTrace.Tests;

/// <summary>全量在线备份：数据库通过 SQLite Backup API，其余 DataRoot 文件逐个做 SHA-256 校验。</summary>
public class DatabaseBackupTests
{
    [Theory]
    [InlineData(2, 30)]
    [InlineData(17, 5)]
    public void Quartz_trigger_uses_configured_daily_local_time(int hour, int minute)
    {
        var trigger = DatabaseBackupScheduleService.CreateDailyTrigger(new TimeOnly(hour, minute));

        Assert.Equal($"0 {minute} {hour} ? * *", trigger.CronExpressionString);
        Assert.Equal(TimeZoneInfo.Local.Id, trigger.TimeZone.Id);
    }

    [Fact]
    public async Task Successful_run_captures_full_data_roots_and_publishes_v2_manifest()
    {
        using var workspace = new TempWorkspace();
        var paths = new DataRootPaths(workspace.Path("data"));
        await CreateSqliteAsync(paths.ConfigDbPath);
        await CreateSqliteAsync(Path.Combine(paths.RuntimeDirectory, "data_202609.db"));
        await WriteFileAsync(Path.Combine(paths.CurvesDirectory, "2026", "curve.json"), "curve");
        await WriteFileAsync(Path.Combine(paths.ArchiveDirectory, "2026", "source.json"), "source");
        await WriteFileAsync(Path.Combine(paths.AuditArchiveDirectory, "2026", "audit.csv.gz"), "audit");
        await WriteFileAsync(Path.Combine(paths.SpoolDirectory, "retry.spool.json"), "{}");

        // Default backup storage is nested under DataRoot and must never back up itself.
        await WriteFileAsync(Path.Combine(paths.Root, "backups", "old-set", "not-data.txt"), "exclude");
        var service = CreateService(paths);

        var result = await service.RunBackupAsync();

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.DatabaseCount);
        Assert.Equal(result.Files.Count, result.VerifiedFileCount);
        Assert.All(result.Files, file =>
        {
            Assert.True(file.IntegrityOk);
            var backupFile = Path.Combine(result.BackupPath!, file.FileName.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(file.SizeBytes, new FileInfo(backupFile).Length);
            Assert.Equal(file.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backupFile))).ToLowerInvariant());
        });
        Assert.Contains(result.Files, file => file.FileName == "config.db" && file.Kind == "sqlite");
        Assert.Contains(result.Files, file => file.FileName == "runtime/data_202609.db" && file.Kind == "sqlite");
        Assert.Contains(result.Files, file => file.FileName == "curves/2026/curve.json");
        Assert.Contains(result.Files, file => file.FileName == "archive/2026/source.json");
        Assert.Contains(result.Files, file => file.FileName == "audit-archive/2026/audit.csv.gz");
        Assert.Contains(result.Files, file => file.FileName == "spool/retry.spool.json");
        Assert.DoesNotContain(result.Files, file => file.FileName.Contains("backups", StringComparison.OrdinalIgnoreCase));

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(result.BackupPath!, "manifest.json")));
        Assert.Equal(2, manifest.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.Equal("full-data", manifest.RootElement.GetProperty("backupType").GetString());
        Assert.True(manifest.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(6, manifest.RootElement.GetProperty("roots").GetArrayLength());
        Assert.Equal(result.Files.Count, manifest.RootElement.GetProperty("files").GetArrayLength());
        Assert.Empty(Directory.GetDirectories(paths.Root + Path.DirectorySeparatorChar + "backups", ".incomplete-*"));
    }

    [Fact]
    public async Task Missing_config_database_fails_without_publishing_a_backup_set()
    {
        using var workspace = new TempWorkspace();
        var paths = new DataRootPaths(workspace.Path("data"));
        var backupRoot = new BackupOptions().ResolveBackupDirectory(paths.Root);
        await WriteFileAsync(Path.Combine(paths.CurvesDirectory, "orphan.curve"), "not enough to recover");
        var service = CreateService(paths);

        // File assets without the required configuration DB are not a restorable full backup.
        var result = await service.RunBackupAsync();

        Assert.False(result.Success);
        Assert.Contains("config.db", result.Error);
        Assert.False(
            Directory.Exists(backupRoot) && Directory.GetDirectories(backupRoot).Length > 0,
            "失败不应留下可被误认为有效备份的正式目录");

        var status = service.GetStatus();
        Assert.Null(status.LastBackupPath);
        Assert.False(status.LastSucceeded);
    }

    [Fact]
    public async Task Backup_waits_for_the_shared_business_operation_barrier()
    {
        using var workspace = new TempWorkspace();
        var paths = new DataRootPaths(workspace.Path("data"));
        await CreateSqliteAsync(paths.ConfigDbPath);
        var barrier = new DataMaintenanceBarrier();
        var service = CreateService(paths, barrier);

        var activeOperation = await barrier.EnterReadAsync();
        var backupTask = service.RunBackupAsync();
        await Task.Delay(100);
        Assert.False(backupTask.IsCompleted);

        await activeOperation.DisposeAsync();
        var result = await backupTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success, result.Error);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task Synthetic_backup_records_real_maintenance_and_waiting_read_durations()
    {
        using var workspace = new TempWorkspace();
        var paths = new DataRootPaths(workspace.Path("data"));
        await CreateSqliteAsync(paths.ConfigDbPath);
        var bytes = ReadBoundedInt("DATATRACE_BACKUP_BENCHMARK_BYTES", 8 * 1024 * 1024, 1, 512 * 1024 * 1024);
        var fileCount = Math.Min(bytes, ReadBoundedInt("DATATRACE_BACKUP_BENCHMARK_FILES", 4, 1, 1000));
        var perFile = Math.Max(1, (bytes + fileCount - 1) / fileCount);
        var payload = new byte[perFile];
        new Random(1729).NextBytes(payload);
        for (var index = 0; index < fileCount; index++)
        {
            var length = bytes / fileCount + (index < bytes % fileCount ? 1 : 0);
            await WriteBytesAsync(Path.Combine(paths.ArchiveDirectory, "benchmark", $"payload-{index:D4}.bin"), payload.AsMemory(0, length));
        }

        var barrier = new MeasuringMaintenanceBarrier();
        var service = CreateService(paths, barrier);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var backup = service.RunBackupAsync();
        await barrier.WriterAcquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var readLeaseTask = barrier.EnterReadAsync().AsTask();
        var result = await backup.WaitAsync(TimeSpan.FromMinutes(2));
        watch.Stop();
        Assert.True(result.Success, result.Error);
        var readLease = await readLeaseTask.WaitAsync(TimeSpan.FromSeconds(10));
        await readLease.DisposeAsync();

        var measurement = new
        {
            payloadBytes = bytes,
            payloadFiles = fileCount,
            sqliteDatabases = result.DatabaseCount,
            verifiedFiles = result.VerifiedFileCount,
            backupElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            maintenanceExclusiveMilliseconds = barrier.WriteHoldMilliseconds,
            waitingReadLeaseMilliseconds = barrier.ReadWaitMilliseconds,
            measurement = "local synthetic-data run; not a customer-data estimate"
        };
        var json = System.Text.Json.JsonSerializer.Serialize(measurement, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("BACKUP_BENCHMARK_JSON=" + json);
        var output = Environment.GetEnvironmentVariable("DATATRACE_BACKUP_BENCHMARK_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, json);
        }

        Assert.True(measurement.maintenanceExclusiveMilliseconds >= 0);
        Assert.True(measurement.waitingReadLeaseMilliseconds >= 0);
    }

    private static int ReadBoundedInt(string name, int fallback, int minimum, int maximum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value >= minimum && value <= maximum ? value : fallback;
    }

    private static async Task WriteBytesAsync(string path, ReadOnlyMemory<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var output = File.Create(path);
        await output.WriteAsync(bytes);
    }

    private static DatabaseBackupService CreateService(DataRootPaths paths, IDataMaintenanceBarrier? barrier = null) =>
        new(
            paths,
            new StubBackupOptions(new BackupOptions()),
            new RuntimeDbFactory(paths.RuntimeDirectory),
            barrier ?? new DataMaintenanceBarrier(),
            NullLogger<DatabaseBackupService>.Instance);

    private sealed class MeasuringMaintenanceBarrier : IDataMaintenanceBarrier
    {
        private readonly DataMaintenanceBarrier _inner = new();
        public TaskCompletionSource<bool> WriterAcquired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public double WriteHoldMilliseconds { get; private set; }
        public double ReadWaitMilliseconds { get; private set; }

        public async ValueTask<IAsyncDisposable> EnterWriteAsync(CancellationToken cancellationToken = default)
        {
            var lease = await _inner.EnterWriteAsync(cancellationToken);
            WriterAcquired.TrySetResult(true);
            return new TimedLease(lease, elapsed => WriteHoldMilliseconds = elapsed);
        }

        public async ValueTask<IAsyncDisposable> EnterReadAsync(CancellationToken cancellationToken = default)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var lease = await _inner.EnterReadAsync(cancellationToken);
            ReadWaitMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return lease;
        }

        private sealed class TimedLease(IAsyncDisposable inner, Action<double> onDispose) : IAsyncDisposable
        {
            private readonly long _acquiredAt = System.Diagnostics.Stopwatch.GetTimestamp();
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                onDispose(System.Diagnostics.Stopwatch.GetElapsedTime(_acquiredAt).TotalMilliseconds);
            }
        }
    }

    private static async Task CreateSqliteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sample (value TEXT); INSERT INTO sample VALUES ('ok');";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WriteFileAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private sealed class StubBackupOptions(BackupOptions value) : IOptionsMonitor<BackupOptions>
    {
        public BackupOptions CurrentValue { get; } = value;

        public BackupOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<BackupOptions, string?> listener) => null;
    }
}
