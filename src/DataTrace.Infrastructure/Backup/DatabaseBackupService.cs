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

public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly TimeSpan CatchUpAge = TimeSpan.FromHours(24);
    private const int BackupFormatVersion = 2;
    private static readonly string[] DataDirectories = ["runtime", "curves", "archive", "audit-archive", "spool"];

    private readonly DataRootPaths _paths;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly RuntimeDbFactory _runtime;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly StatusState _state = new();

    public DatabaseBackupService(
        DataRootPaths paths,
        IOptionsMonitor<BackupOptions> options,
        RuntimeDbFactory runtime,
        ILogger<DatabaseBackupService> logger)
    {
        _paths = paths;
        _options = options;
        _runtime = runtime;
        _logger = logger;
        TryLoadStatusFromDisk();
    }

    public BackupStatusSnapshot GetStatus()
    {
        var opts = _options.CurrentValue;
        lock (_statusGate)
        {
            return new BackupStatusSnapshot
            {
                Enabled = opts.Enabled,
                DailyTime = opts.DailyTime,
                BackupDirectory = opts.ResolveBackupDirectory(_paths.Root),
                RetentionDays = opts.RetentionDays,
                MaxBackups = opts.MaxBackups,
                LastSuccessAt = _state.LastSuccessAt,
                LastAttemptAt = _state.LastAttemptAt,
                LastSucceeded = _state.LastSucceeded,
                LastError = _state.LastError,
                LastBackupPath = _state.LastBackupPath,
                LastBackupBytes = _state.LastBackupBytes,
                LastFileCount = _state.LastFileCount,
                LastDatabaseCount = _state.LastDatabaseCount,
                LastVerifiedFileCount = _state.LastVerifiedFileCount,
                LastBackupFormatVersion = _state.LastBackupFormatVersion,
                NextScheduledAt = ComputeNextScheduled(opts)
            };
        }
    }

    public bool NeedsCatchUpBackup()
    {
        if (!_options.CurrentValue.Enabled)
        {
            return false;
        }

        lock (_statusGate)
        {
            if (_state.LastSuccessAt is null)
            {
                return true;
            }

            return DateTimeOffset.Now - _state.LastSuccessAt.Value >= CatchUpAge;
        }
    }

    public async Task<BackupRunResult> RunBackupAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BackupRunResult
            {
                Success = false,
                Error = "已有备份任务在执行，请稍后再试",
                StartedAt = DateTimeOffset.Now,
                FinishedAt = DateTimeOffset.Now
            };
        }

        var started = DateTimeOffset.Now;
        string? incompleteSet = null;
        try
        {
            var opts = _options.CurrentValue;
            var backupRoot = opts.ResolveBackupDirectory(_paths.Root);
            Directory.CreateDirectory(backupRoot);

            var stamp = started.ToLocalTime().ToString("yyyy-MM-dd_HHmm");
            var setDir = Path.Combine(backupRoot, stamp);
            var unique = setDir;
            for (var i = 1; Directory.Exists(unique); i++)
            {
                unique = $"{setDir}_{i}";
            }

            incompleteSet = Path.Combine(backupRoot, $".incomplete-{Guid.NewGuid():N}");
            Directory.CreateDirectory(incompleteSet);
            foreach (var directory in DataDirectories)
            {
                Directory.CreateDirectory(Path.Combine(incompleteSet, directory));
            }

            var sources = DiscoverSqliteFiles();
            if (!sources.Any(source => string.Equals(source.LogicalName, "config.db", StringComparison.OrdinalIgnoreCase)))
            {
                const string emptyMsg = "缺少必需的配置数据库 config.db；不能创建可恢复备份";
                _logger.LogWarning("{Error}", emptyMsg);
                TryDeleteSet(incompleteSet);
                incompleteSet = null;
                PersistStatus(started, false, emptyMsg, null, 0, 0, 0, 0);
                return new BackupRunResult
                {
                    Success = false,
                    Error = emptyMsg,
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.Now,
                    BackupPath = null
                };
            }

            var files = new List<BackupFileInfo>();
            long total = 0;
            foreach (var src in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destPath = ResolveChildPath(incompleteSet, src.LogicalName);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await BackupSqliteOnlineAsync(src.FullPath, destPath, cancellationToken).ConfigureAwait(false);

                var (ok, detail) = await QuickCheckAsync(destPath, cancellationToken).ConfigureAwait(false);
                var sha = await Sha256FileAsync(destPath, cancellationToken).ConfigureAwait(false);
                var len = new FileInfo(destPath).Length;
                total += len;
                files.Add(new BackupFileInfo
                {
                    FileName = src.LogicalName.Replace('\\', '/'),
                    Kind = "sqlite",
                    SizeBytes = len,
                    Sha256 = sha,
                    IntegrityOk = ok,
                    IntegrityDetail = detail
                });

                if (!ok)
                {
                    var err = $"完整性校验失败: {src.LogicalName}: {detail}";
                    _logger.LogError("{Error}", err);
                    TryDeleteSet(incompleteSet);
                    incompleteSet = null;
                    PersistStatus(started, false, err, null, total, files.Count, sources.Count, files.Count(f => f.IntegrityOk));
                    return new BackupRunResult
                    {
                        Success = false,
                        Error = err,
                        StartedAt = started,
                        FinishedAt = DateTimeOffset.Now,
                        BackupPath = null,
                        TotalBytes = total,
                        DatabaseCount = sources.Count,
                        VerifiedFileCount = files.Count(f => f.IntegrityOk),
                        Files = files
                    };
                }
            }

            foreach (var directory in DataDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceDirectory = Path.Combine(_paths.Root, directory);
                if (!Directory.Exists(sourceDirectory))
                {
                    continue;
                }

                foreach (var sourcePath in EnumerateDataFiles(sourceDirectory, directory, opts.ResolveBackupDirectory(_paths.Root)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var logicalName = Path.GetRelativePath(_paths.Root, sourcePath).Replace('\\', '/');
                    var destination = ResolveChildPath(incompleteSet, logicalName);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var (length, sha) = await CopyAndHashAsync(sourcePath, destination, cancellationToken).ConfigureAwait(false);
                    total += length;
                    files.Add(new BackupFileInfo
                    {
                        FileName = logicalName,
                        Kind = "file",
                        SizeBytes = length,
                        Sha256 = sha,
                        IntegrityOk = true,
                        IntegrityDetail = "sha256"
                    });
                }
            }

            var verifiedCount = files.Count(file => file.IntegrityOk);
            var appVersion = typeof(DatabaseBackupService).Assembly.GetName().Version?.ToString() ?? "";
            var manifest = new
            {
                formatVersion = BackupFormatVersion,
                backupType = "full-data",
                complete = true,
                appVersion,
                createdAt = started.ToString("o"),
                finishedAt = DateTimeOffset.Now.ToString("o"),
                dataRoot = _paths.Root,
                roots = new[] { "config.db" }.Concat(DataDirectories).ToArray(),
                files = files.Select(f => new
                {
                    file = f.FileName,
                    kind = f.Kind,
                    sizeBytes = f.SizeBytes,
                    sha256 = f.Sha256,
                    integrity = f.IntegrityOk ? "ok" : f.IntegrityDetail
                }).ToList()
            };
            await File.WriteAllTextAsync(
                Path.Combine(incompleteSet, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOpts),
                cancellationToken).ConfigureAwait(false);

            // Publish only a complete, verified set; restore -Latest ignores incomplete staging folders.
            Directory.Move(incompleteSet, unique);
            incompleteSet = null;
            var deletedSets = ApplyRetention(backupRoot, opts, unique);

            PersistStatus(started, true, null, unique, total, files.Count, sources.Count, verifiedCount);
            _logger.LogInformation(
                "全量数据备份成功：{Path}，{DatabaseCount} 个数据库，{FileCount} 个文件，{Bytes} 字节，清理旧备份集 {Deleted}",
                unique, sources.Count, files.Count, total, deletedSets);

            return new BackupRunResult
            {
                Success = true,
                StartedAt = started,
                FinishedAt = DateTimeOffset.Now,
                BackupPath = unique,
                TotalBytes = total,
                DatabaseCount = sources.Count,
                VerifiedFileCount = verifiedCount,
                Files = files,
                DeletedBackupSets = deletedSets
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量数据备份失败");
            TryDeleteSet(incompleteSet);
            PersistStatus(started, false, ex.Message, null, 0, 0, 0, 0);
            return new BackupRunResult
            {
                Success = false,
                Error = ex.Message,
                StartedAt = started,
                FinishedAt = DateTimeOffset.Now
            };
        }
        finally
        {
            _gate.Release();
        }
    }

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

    private List<(string LogicalName, string FullPath)> DiscoverSqliteFiles()
    {
        var list = new List<(string, string)>();
        if (File.Exists(_paths.ConfigDbPath))
        {
            list.Add(("config.db", _paths.ConfigDbPath));
        }

        if (Directory.Exists(_paths.RuntimeDirectory))
        {
            foreach (var file in Directory.GetFiles(_paths.RuntimeDirectory, "data_*.db").OrderBy(x => x))
            {
                list.Add((Path.Combine("runtime", Path.GetFileName(file)), file));
            }
        }

        return list;
    }

    private static IEnumerable<string> EnumerateDataFiles(string sourceRoot, string logicalRoot, string backupRoot)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var fullPath = Path.GetFullPath(entry);
                if (IsSameOrChildPath(fullPath, backupRoot))
                {
                    continue;
                }

                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"数据目录包含不支持的链接/重解析点：{fullPath}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(fullPath);
                    continue;
                }

                var relative = Path.GetRelativePath(sourceRoot, fullPath);
                if (string.Equals(logicalRoot, "runtime", StringComparison.OrdinalIgnoreCase)
                    && !relative.Contains(Path.DirectorySeparatorChar)
                    && IsRuntimeDatabaseArtifact(Path.GetFileName(fullPath)))
                {
                    // Runtime monthly DBs were captured with SQLite Backup API; never copy live WAL/SHM sidecars.
                    continue;
                }

                yield return fullPath;
            }
        }
    }

    private static bool IsRuntimeDatabaseArtifact(string fileName) =>
        fileName.StartsWith("data_", StringComparison.OrdinalIgnoreCase)
        && (fileName.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrChildPath(string path, string parent)
    {
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedParent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveChildPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"备份路径越界：{relativePath}");
        }

        return fullPath;
    }

    private static async Task<(long Length, string Sha256)> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var sourceBefore = new FileInfo(sourcePath);
        var initialLength = sourceBefore.Length;
        var initialWriteTime = sourceBefore.LastWriteTimeUtc;

        await using (var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true))
        await using (var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        sourceBefore.Refresh();
        var copiedLength = new FileInfo(destinationPath).Length;
        if (!sourceBefore.Exists
            || sourceBefore.Length != initialLength
            || sourceBefore.LastWriteTimeUtc != initialWriteTime
            || copiedLength != initialLength)
        {
            throw new IOException($"Source file changed while being backed up: {sourcePath}");
        }

        return (copiedLength, await Sha256FileAsync(destinationPath, cancellationToken).ConfigureAwait(false));
    }

    private static async Task BackupSqliteOnlineAsync(string sourcePath, string destPath, CancellationToken ct)
    {
        // Pooling=false：避免连接池在 Backup/校验后仍占用目标文件，导致 SHA256 读失败。
        await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        await using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            await source.OpenAsync(ct).ConfigureAwait(false);
            await dest.OpenAsync(ct).ConfigureAwait(false);
            source.BackupDatabase(dest);
            await using var cmd = dest.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var side in new[] { destPath + "-wal", destPath + "-shm" })
        {
            try
            {
                if (File.Exists(side))
                {
                    File.Delete(side);
                }
            }
            catch
            {
                // best-effort
            }
        }
    }

    private static async Task<(bool Ok, string Detail)> QuickCheckAsync(string dbPath, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var result = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)
                ? (true, "ok")
                : (false, result ?? "unknown");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
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

    private sealed class StatusState
    {
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? LastAttemptAt { get; set; }
        public bool? LastSucceeded { get; set; }
        public string? LastError { get; set; }
        public string? LastBackupPath { get; set; }
        public long LastBackupBytes { get; set; }
        public int LastFileCount { get; set; }
        public int LastDatabaseCount { get; set; }
        public int LastVerifiedFileCount { get; set; }
        public int LastBackupFormatVersion { get; set; }
    }
}
