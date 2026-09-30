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
}
