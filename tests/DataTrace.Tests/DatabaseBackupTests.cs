using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Infrastructure.Backup;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataTrace.Tests;

/// <summary>
/// 在线备份：SQLite Backup API 备份 config.db 与 runtime 月库。
/// 这里只覆盖"没有可备份对象"的失败路径 —— 它此前会留下一个空目录，
/// 而保留策略只清成功备份，现场会看到一串"像是备份过"的空目录。
/// </summary>
public class DatabaseBackupTests
{
    [Fact]
    public async Task Failed_run_leaves_no_backup_directory_behind()
    {
        using var workspace = new TempWorkspace();
        var paths = new DataRootPaths(workspace.Path("data"));
        var backupRoot = new BackupOptions().ResolveBackupDirectory(paths.Root);
        var service = new DatabaseBackupService(
            paths,
            new StubBackupOptions(new BackupOptions()),
            new RuntimeDbFactory(paths.RuntimeDirectory),
            NullLogger<DatabaseBackupService>.Instance);

        // 没有任何 config.db / 月库：这一轮必然失败。
        var result = await service.RunBackupAsync();

        Assert.False(result.Success);
        Assert.Contains("未找到可备份", result.Error);
        Assert.False(
            Directory.Exists(backupRoot) && Directory.GetDirectories(backupRoot).Length > 0,
            "失败不该留下空的备份集目录");

        // 失败也不该把状态里的备份路径指向一个已被删掉的目录。
        var status = service.GetStatus();
        Assert.Null(status.LastBackupPath);
        Assert.False(status.LastSucceeded);
    }

    private sealed class StubBackupOptions(BackupOptions value) : IOptionsMonitor<BackupOptions>
    {
        public BackupOptions CurrentValue { get; } = value;

        public BackupOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<BackupOptions, string?> listener) => null;
    }
}
