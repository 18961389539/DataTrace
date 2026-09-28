using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Infrastructure.Backup;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataTrace.Tests;

/// <summary>全量在线备份：数据库通过 SQLite Backup API，其余 DataRoot 文件逐个做 SHA-256 校验。</summary>
public class DatabaseBackupTests
{
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

    private static DatabaseBackupService CreateService(DataRootPaths paths) =>
        new(
            paths,
            new StubBackupOptions(new BackupOptions()),
            new RuntimeDbFactory(paths.RuntimeDirectory),
            NullLogger<DatabaseBackupService>.Instance);

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
