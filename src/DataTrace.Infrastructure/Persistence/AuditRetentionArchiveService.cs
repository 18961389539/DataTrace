using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataTrace.Application.Configuration;
using DataTrace.Domain.Entities;
using DataTrace.Infrastructure.Backup;
using Microsoft.Extensions.Logging;

namespace DataTrace.Infrastructure.Persistence;

public sealed class AuditRetentionArchiveService
{
    private const int BatchSize = 2000;
    private readonly IAuditLogger _audit;
    private readonly DataRootPaths _paths;
    private readonly ILogger<AuditRetentionArchiveService> _logger;

    public AuditRetentionArchiveService(
        IAuditLogger audit,
        DataRootPaths paths,
        ILogger<AuditRetentionArchiveService> logger)
    {
        _audit = audit;
        _paths = paths;
        _logger = logger;
    }

    public async Task<int> ArchiveAndPurgeAsync(int retentionYears, CancellationToken cancellationToken = default)
    {
        if (retentionYears <= 0)
        {
            return 0;
        }

        var cutoff = DateTime.Now.AddYears(-retentionYears);
        var endExclusive = new DateTime(cutoff.Year, cutoff.Month, 1);
        var oldest = await _audit.GetOldestTimeAsync(cancellationToken).ConfigureAwait(false);
        if (oldest is null)
        {
            return 0;
        }

        var month = new DateTime(oldest.Value.Year, oldest.Value.Month, 1);
        var latestId = await _audit.GetLatestIdAsync(cancellationToken).ConfigureAwait(false);
        var deletedTotal = 0;
        Directory.CreateDirectory(_paths.AuditArchiveDirectory);

        while (month < endExclusive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var monthEnd = month.AddMonths(1);
            var monthKey = month.ToString("yyyyMM");
            try
            {
                while (true)
                {
                    var range = await _audit.QueryAsync(
                        fromInclusive: month,
                        toInclusive: monthEnd.AddTicks(-1),
                        take: 1,
                        idAtMost: latestId,
                        newestFirst: false,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (range.Total == 0)
                    {
                        break;
                    }

                    var archive = await FindRecoverableArchiveAsync(month, monthEnd, latestId, cancellationToken)
                        .ConfigureAwait(false)
                        ?? await EnsureArchiveAsync(month, monthEnd, latestId, range.Total, cancellationToken)
                            .ConfigureAwait(false);
                    var deleted = await _audit.DeleteRangeAsync(month, monthEnd, archive.IdAtMost, cancellationToken)
                        .ConfigureAwait(false);

                    if (deleted > 0)
                    {
                        await _audit.WriteAsync(
                            "system",
                            "ArchiveAndPurge",
                            "AuditLog",
                            monthKey,
                            null,
                            $"rows={deleted}; archive={archive.FileName}; sha256={archive.Sha256}",
                            cancellationToken,
                            source: "Audit retention service",
                            correlationId: $"audit-retention-{monthKey}-{archive.IdAtMost}")
                            .ConfigureAwait(false);
                    }

                    deletedTotal += deleted;
                    _logger.LogInformation(
                        "已归档并清理 {Month} 审计日志 {Count} 条；SHA-256={Sha256}",
                        monthKey, deleted, archive.Sha256);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                try
                {
                    await _audit.WriteAsync(
                        "system",
                        "ArchiveAndPurge",
                        "AuditLog",
                        monthKey,
                        null,
                        $"failure; {ex.Message}",
                        cancellationToken,
                        outcome: "Failure",
                        source: "Audit retention service",
                        correlationId: $"audit-retention-{monthKey}-{Guid.NewGuid():N}")
                        .ConfigureAwait(false);
                }
                catch (Exception auditException) when (auditException is not OperationCanceledException)
                {
                    _logger.LogError(auditException, "审计归档月份 {Month} 失败，且失败审计写入失败", monthKey);
                }

                throw;
            }

            month = monthEnd;
        }

        return deletedTotal;
    }

    private async Task<ArchiveManifest?> FindRecoverableArchiveAsync(
        DateTime monthStart,
        DateTime monthEnd,
        long latestId,
        CancellationToken cancellationToken)
    {
        var monthKey = monthStart.ToString("yyyyMM");
        var monthDirectory = Path.Combine(_paths.AuditArchiveDirectory, monthKey);
        if (!Directory.Exists(monthDirectory))
        {
            return null;
        }

        foreach (var batchDirectory in Directory.EnumerateDirectories(monthDirectory, "batch-*"))
        {
            var manifest = await ReadAndVerifyArchiveAsync(batchDirectory, monthKey, cancellationToken)
                .ConfigureAwait(false);
            if (manifest.IdAtMost > latestId)
            {
                continue;
            }

            var stats = await _audit.GetRangeStatsAsync(
                monthStart, monthEnd, manifest.IdAtMost, cancellationToken).ConfigureAwait(false);
            if (stats.Count == manifest.Rows
                && stats.MinId == manifest.MinId
                && stats.MaxId == manifest.MaxId)
            {
                return manifest;
            }
        }

        return null;
    }

    private async Task<ArchiveManifest> EnsureArchiveAsync(
        DateTime monthStart,
        DateTime monthEnd,
        long idAtMost,
        int expectedRows,
        CancellationToken cancellationToken)
    {
        var monthKey = monthStart.ToString("yyyyMM");
        var monthDirectory = Path.Combine(_paths.AuditArchiveDirectory, monthKey);
        Directory.CreateDirectory(monthDirectory);
        var tempDirectory = Path.Combine(
            monthDirectory,
            $".{monthKey}-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var fileName = $"AuditLogs_{monthKey}.csv.gz";
            var archivePath = Path.Combine(tempDirectory, fileName);
            var (rows, minId, maxId) = await WriteArchiveAsync(
                archivePath, monthStart, monthEnd, idAtMost, cancellationToken).ConfigureAwait(false);

            if (rows != expectedRows)
            {
                throw new InvalidDataException($"审计归档 {monthKey} 行数变化，已保留在线记录。");
            }

            var sha256 = await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false);
            var manifest = new ArchiveManifest(monthKey, fileName, rows, minId, maxId, idAtMost, sha256, DateTime.UtcNow);
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);

            var finalDirectory = Path.Combine(
                monthDirectory,
                $"batch-{idAtMost}-{minId}-{maxId}");
            if (Directory.Exists(finalDirectory))
            {
                var existing = await ReadAndVerifyArchiveAsync(finalDirectory, monthKey, cancellationToken)
                    .ConfigureAwait(false);
                if (existing.Rows != rows
                    || existing.MinId != minId
                    || existing.MaxId != maxId
                    || existing.IdAtMost != idAtMost)
                {
                    throw new InvalidDataException($"审计归档 {monthKey} 批次冲突，已保留在线记录。");
                }
            }
            else
            {
                Directory.Move(tempDirectory, finalDirectory);
            }

            return manifest;
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private async Task<ArchiveManifest> ReadAndVerifyArchiveAsync(
        string directory,
        string monthKey,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"审计归档 {monthKey} 缺少清单，已保留在线记录。");
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(json)
            ?? throw new InvalidDataException($"审计归档 {monthKey} 清单无效，已保留在线记录。");
        if (manifest.Rows <= 0
            || manifest.MinId <= 0
            || manifest.MaxId < manifest.MinId
            || manifest.IdAtMost < manifest.MaxId
            || string.IsNullOrWhiteSpace(manifest.FileName)
            || Path.GetFileName(manifest.FileName) != manifest.FileName)
        {
            throw new InvalidDataException($"审计归档 {monthKey} 清单范围无效，已保留在线记录。");
        }

        var archivePath = Path.Combine(directory, manifest.FileName);
        if (manifest.Month != monthKey
            || !File.Exists(archivePath)
            || !string.Equals(
                manifest.Sha256,
                await ComputeSha256Async(archivePath, cancellationToken).ConfigureAwait(false),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"审计归档 {monthKey} 校验失败，已保留在线记录。");
        }

        return manifest;
    }

    private async Task<(int Rows, long MinId, long MaxId)> WriteArchiveAsync(
        string path,
        DateTime monthStart,
        DateTime monthEnd,
        long idAtMost,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(true), 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync(CsvRow([
            "Id", "Time", "UserName", "Action", "EntityType", "EntityKey",
            "Outcome", "Source", "SourceIp", "CorrelationId", "OldValue", "NewValue"
        ]));

        var rows = 0;
        var minId = long.MaxValue;
        var maxId = 0L;
        while (true)
        {
            var (items, _) = await _audit.QueryAsync(
                fromInclusive: monthStart,
                skip: rows,
                take: BatchSize,
                toInclusive: monthEnd.AddTicks(-1),
                idAtMost: idAtMost,
                newestFirst: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                await writer.WriteLineAsync(CsvRow(Values(item)));
                minId = Math.Min(minId, item.Id);
                maxId = Math.Max(maxId, item.Id);
            }

            rows += items.Count;
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (rows, rows == 0 ? 0 : minId, maxId);
    }

    private static IEnumerable<string?> Values(AuditLog item) =>
    [
        item.Id.ToString(),
        item.Time.ToString("O"),
        item.UserName,
        item.Action,
        item.EntityType,
        item.EntityKey,
        item.Outcome,
        item.Source,
        item.SourceIp,
        item.CorrelationId,
        item.OldValue,
        item.NewValue
    ];

    private static string CsvRow(IEnumerable<string?> values) => string.Join(',', values.Select(Escape));

    private static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '@')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) < 0
            ? text
            : "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private sealed record ArchiveManifest(
        string Month,
        string FileName,
        int Rows,
        long MinId,
        long MaxId,
        long IdAtMost,
        string Sha256,
        DateTime CreatedAtUtc);
}
