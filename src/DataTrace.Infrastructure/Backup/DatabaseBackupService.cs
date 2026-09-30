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

// 按职责拆成 partial 多文件：备份主流程本文件，文件与 SQLite 底层见 .Files.cs，
// 保留策略与状态持久化见 .Retention.cs。
public sealed partial class DatabaseBackupService : IDatabaseBackupService
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

