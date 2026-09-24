using System.Security.Cryptography;
using System.Text.Json;
using DataTrace.Application.Backup;
using DataTrace.Application.Configuration;
using DataTrace.Application.Runtime;
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

    private readonly DataRootPaths _paths;
    private readonly IOptionsMonitor<BackupOptions> _options;
    private readonly RuntimeDbFactory _runtime;
    private readonly ICurveFileStore _curves;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly StatusState _state = new();

    public DatabaseBackupService(
        DataRootPaths paths,
        IOptionsMonitor<BackupOptions> options,
        RuntimeDbFactory runtime,
        ICurveFileStore curves,
        ILogger<DatabaseBackupService> logger)
    {
        _paths = paths;
        _options = options;
        _runtime = runtime;
        _curves = curves;
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
                RecordRetentionEnabled = opts.RecordRetention.Enabled,
                RecordKeepDays = opts.RecordRetention.KeepDays,
                LastSuccessAt = _state.LastSuccessAt,
                LastAttemptAt = _state.LastAttemptAt,
                LastSucceeded = _state.LastSucceeded,
                LastError = _state.LastError,
                LastBackupPath = _state.LastBackupPath,
                LastBackupBytes = _state.LastBackupBytes,
                LastFileCount = _state.LastFileCount,
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

            Directory.CreateDirectory(unique);

            var sources = DiscoverSqliteFiles();
            if (sources.Count == 0)
            {
                const string emptyMsg = "未找到可备份的 SQLite 数据库（config.db / runtime/data_*.db）";
                _logger.LogWarning(emptyMsg);
                PersistStatus(started, false, emptyMsg, unique, 0, 0);
                return new BackupRunResult
                {
                    Success = false,
                    Error = emptyMsg,
                    StartedAt = started,
                    FinishedAt = DateTimeOffset.Now,
                    BackupPath = unique
                };
            }

            var files = new List<BackupFileInfo>();
            long total = 0;
            foreach (var src in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destPath = Path.Combine(unique, src.LogicalName);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                await BackupSqliteOnlineAsync(src.FullPath, destPath, cancellationToken).ConfigureAwait(false);

                var (ok, detail) = await QuickCheckAsync(destPath, cancellationToken).ConfigureAwait(false);
                var sha = await Sha256FileAsync(destPath, cancellationToken).ConfigureAwait(false);
                var len = new FileInfo(destPath).Length;
                total += len;
                files.Add(new BackupFileInfo
                {
                    FileName = src.LogicalName.Replace('\\', '/'),
                    SizeBytes = len,
                    Sha256 = sha,
                    IntegrityOk = ok,
                    IntegrityDetail = detail
                });

                if (!ok)
                {
                    var err = $"完整性校验失败: {src.LogicalName}: {detail}";
                    _logger.LogError("{Error}", err);
                    PersistStatus(started, false, err, unique, total, files.Count);
                    return new BackupRunResult
                    {
                        Success = false,
                        Error = err,
                        StartedAt = started,
                        FinishedAt = DateTimeOffset.Now,
                        BackupPath = unique,
                        TotalBytes = total,
                        Files = files
                    };
                }
            }

            var appVersion = typeof(DatabaseBackupService).Assembly.GetName().Version?.ToString() ?? "";
            var manifest = new
            {
                appVersion,
                createdAt = started.ToString("o"),
                finishedAt = DateTimeOffset.Now.ToString("o"),
                dataRoot = _paths.Root,
                files = files.Select(f => new
                {
                    file = f.FileName,
                    sizeBytes = f.SizeBytes,
                    sha256 = f.Sha256,
                    integrity = f.IntegrityOk ? "ok" : f.IntegrityDetail
                }).ToList()
            };
            await File.WriteAllTextAsync(
                Path.Combine(unique, "manifest.json"),
                JsonSerializer.Serialize(manifest, JsonOpts),
                cancellationToken).ConfigureAwait(false);

            var deletedSets = ApplyRetention(backupRoot, opts, unique);

            RecordPurgeResult? purge = null;
            if (opts.RecordRetention.Enabled)
            {
                purge = await PurgeOldRecordsAsync(opts.RecordRetention.KeepDays, cancellationToken)
                    .ConfigureAwait(false);
                if (purge.Error is not null)
                {
                    _logger.LogWarning("记录清理未完全成功: {Error}", purge.Error);
                }
                else
                {
                    _logger.LogInformation(
                        "记录清理完成：删除 {Records} 条采集记录，{Curves} 个曲线文件（保留 {Days} 天）",
                        purge.DeletedRecords, purge.DeletedCurveFiles, opts.RecordRetention.KeepDays);
                }
            }

            PersistStatus(started, true, null, unique, total, files.Count);
            _logger.LogInformation(
                "数据库备份成功：{Path}，{Count} 个文件，{Bytes} 字节，清理旧备份集 {Deleted}",
                unique, files.Count, total, deletedSets);

            return new BackupRunResult
            {
                Success = true,
                StartedAt = started,
                FinishedAt = DateTimeOffset.Now,
                BackupPath = unique,
                TotalBytes = total,
                Files = files,
                DeletedBackupSets = deletedSets,
                RecordPurge = purge
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库备份失败");
            PersistStatus(started, false, ex.Message, null, 0, 0);
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

    private async Task<RecordPurgeResult> PurgeOldRecordsAsync(int keepDays, CancellationToken ct)
    {
        keepDays = Math.Max(1, keepDays);
        var cutoff = DateTime.Now.Date.AddDays(-keepDays);
        var deletedRecords = 0;
        var deletedCurves = 0;
        try
        {
            foreach (var month in _runtime.ListMonthKeys())
            {
                if (month.Length == 6
                    && DateTime.TryParseExact(month + "01", "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var monthStart)
                    && monthStart.AddMonths(1) <= cutoff)
                {
                    await using (var db = _runtime.Open(month))
                    {
                        deletedRecords += await db.CollectRecords.CountAsync(ct).ConfigureAwait(false);
                    }

                    _runtime.DeleteMonth(month);
                    await _curves.DeleteMonthAsync(month[..4], month[4..], ct).ConfigureAwait(false);
                    _logger.LogInformation("记录保留：已删除整月 {Month}", month);
                    continue;
                }

                await using var ctx = _runtime.Open(month);
                const int batch = 200;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var ids = await ctx.CollectRecords.AsNoTracking()
                        .Where(r => r.TriggerTime < cutoff)
                        .OrderBy(r => r.Id)
                        .Take(batch)
                        .Select(r => r.Id)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);
                    if (ids.Count == 0)
                    {
                        break;
                    }

                    var curvePaths = await ctx.CurveRecords.AsNoTracking()
                        .Where(c => ids.Contains(c.CollectRecordId))
                        .Select(c => c.RelativePath)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    foreach (var rel in curvePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
                    {
                        await _curves.DeleteFileAsync(rel, ct).ConfigureAwait(false);
                        deletedCurves++;
                    }

                    var curveIds = await ctx.CurveRecords.AsNoTracking()
                        .Where(c => ids.Contains(c.CollectRecordId))
                        .Select(c => c.Id)
                        .ToListAsync(ct)
                        .ConfigureAwait(false);

                    if (curveIds.Count > 0)
                    {
                        await ctx.CurveFeatures.Where(f => curveIds.Contains(f.CurveRecordId))
                            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    }

                    await ctx.CurveRecords.Where(c => ids.Contains(c.CollectRecordId))
                        .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    await ctx.TagValues.Where(t => ids.Contains(t.CollectRecordId))
                        .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    await ctx.ProductRecords.Where(p => ids.Contains(p.CollectRecordId))
                        .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                    deletedRecords += await ctx.CollectRecords.Where(r => ids.Contains(r.Id))
                        .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                }
            }

            return new RecordPurgeResult
            {
                Ran = true,
                DeletedRecords = deletedRecords,
                DeletedCurveFiles = deletedCurves
            };
        }
        catch (Exception ex)
        {
            return new RecordPurgeResult
            {
                Ran = true,
                DeletedRecords = deletedRecords,
                DeletedCurveFiles = deletedCurves,
                Error = ex.Message
            };
        }
    }

    private void PersistStatus(
        DateTimeOffset started,
        bool success,
        string? error,
        string? path,
        long bytes,
        int fileCount)
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
    }
}
