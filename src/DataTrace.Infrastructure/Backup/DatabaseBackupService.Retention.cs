using System.Security.Cryptography;
using System.Text.Json;
using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataTrace.Infrastructure.Backup;

partial class DatabaseBackupService
{
    /// <summary>删除本次未完成的备份集目录（best-effort，失败只记日志）。</summary>
    private void TryDeleteSet(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                _logger.LogInformation("已清理未完成的备份集目录 {Path}", path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理未完成的备份集目录失败 {Path}", path);
        }
    }

    private int ApplyRetention(string backupRoot, BackupOptions opts, string keepNewestPath)
    {
        var sets = Directory.GetDirectories(backupRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !d.Name.StartsWith("pre-restore-", StringComparison.OrdinalIgnoreCase))
            .Where(d => !d.Name.StartsWith("upgrade-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.CreationTimeUtc)
            .ToList();

        var toDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (opts.RetentionDays > 0)
        {
            var cutoff = DateTime.Now.Date.AddDays(-opts.RetentionDays);
            foreach (var d in sets)
            {
                if (d.CreationTime < cutoff && !PathsEqual(d.FullName, keepNewestPath))
                {
                    toDelete.Add(d.FullName);
                }
            }
        }

        if (opts.MaxBackups > 0)
        {
            var remaining = sets
                .Where(d => !toDelete.Contains(d.FullName))
                .OrderBy(d => d.CreationTimeUtc)
                .ToList();
            var overflow = remaining.Count - opts.MaxBackups;
            for (var i = 0; i < overflow; i++)
            {
                if (!PathsEqual(remaining[i].FullName, keepNewestPath))
                {
                    toDelete.Add(remaining[i].FullName);
                }
            }
        }

        var deleted = 0;
        foreach (var path in toDelete)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                deleted++;
                _logger.LogInformation("已按保留策略删除旧备份集 {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "删除旧备份集失败 {Path}", path);
            }
        }

        return deleted;
    }

    private void PersistStatus(
        DateTimeOffset started,
        bool success,
        string? error,
        string? path,
        long bytes,
        int fileCount,
        int databaseCount,
        int verifiedFileCount)
    {
        lock (_statusGate)
        {
            _state.LastAttemptAt = started;
            _state.LastSucceeded = success;
            _state.LastError = error;
            if (success)
            {
                _state.LastSuccessAt = DateTimeOffset.Now;
                _state.LastBackupPath = path;
                _state.LastBackupBytes = bytes;
                _state.LastFileCount = fileCount;
                _state.LastDatabaseCount = databaseCount;
                _state.LastVerifiedFileCount = verifiedFileCount;
                _state.LastBackupFormatVersion = BackupFormatVersion;
            }
            else if (path is not null)
            {
                _state.LastBackupPath = path;
            }
        }

        try
        {
            var dir = _options.CurrentValue.ResolveBackupDirectory(_paths.Root);
            Directory.CreateDirectory(dir);
            var statusPath = Path.Combine(dir, "last-status.json");
            File.WriteAllText(statusPath, JsonSerializer.Serialize(GetStatus(), JsonOpts));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "写入 last-status.json 失败");
        }
    }

    private void TryLoadStatusFromDisk()
    {
        try
        {
            var dir = _options.CurrentValue.ResolveBackupDirectory(_paths.Root);
            var statusPath = Path.Combine(dir, "last-status.json");
            if (!File.Exists(statusPath))
            {
                return;
            }

            var snap = JsonSerializer.Deserialize<BackupStatusSnapshot>(File.ReadAllText(statusPath));
            if (snap is null)
            {
                return;
            }

            lock (_statusGate)
            {
                _state.LastSuccessAt = snap.LastSuccessAt;
                _state.LastAttemptAt = snap.LastAttemptAt;
                _state.LastSucceeded = snap.LastSucceeded;
                _state.LastError = snap.LastError;
                _state.LastBackupPath = snap.LastBackupPath;
                _state.LastBackupBytes = snap.LastBackupBytes;
                _state.LastFileCount = snap.LastFileCount;
                _state.LastDatabaseCount = snap.LastDatabaseCount;
                _state.LastVerifiedFileCount = snap.LastVerifiedFileCount;
                _state.LastBackupFormatVersion = snap.LastBackupFormatVersion;
            }
        }
        catch
        {
            // ignore
        }
    }

    private DateTimeOffset? ComputeNextScheduled(BackupOptions opts)
    {
        if (!opts.Enabled)
        {
            return null;
        }

        var tod = opts.GetDailyTimeOrDefault();
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, tod.Hour, tod.Minute, 0);
        if (next <= now)
        {
            next = next.AddDays(1);
        }

        return new DateTimeOffset(next);
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
